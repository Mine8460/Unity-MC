using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Le monde : il décide quels chunks charger autour du joueur et pilote les threads secondaires.
//
// Principe (comme Minecraft) : le thread principal ne fait JAMAIS le travail lourd.
//   1. il demande la génération d'un chunk (terrain + lumière du chunk) à un thread secondaire ;
//   2. quand le résultat arrive, il l'enregistre (rapide) et échange la lumière avec les voisins ;
//   3. quand un chunk et ses 4 voisins sont prêts, il demande le maillage à un thread secondaire ;
//   4. quand le mesh arrive, il l'envoie à Unity (budget de temps par frame).
// Les chunks apparaissent donc progressivement, du plus proche au plus loin, sans jamais figer le jeu.
public partial class World : MonoBehaviour
{
    [Header("Références")]
    [SerializeField] Material chunkMaterial;
    [Tooltip("Matériau de l'eau (shader Voxel/Water, même atlas que les blocs)")]
    [SerializeField] Material waterMaterial;
    Material translucentMaterial;   // copie de chunkMaterial avec mélange alpha (créée au lancement)
    [Tooltip("Smooth lighting façon Minecraft : lumière lissée et ombre douce dans les coins (pris en compte au chargement)")]
    [SerializeField] bool smoothLighting = true;
    [SerializeField] Transform player;

    [Header("Chargement des chunks")]
    [Tooltip("Rayon de rendu, en chunks (1 chunk = 16 blocs)")]
    [SerializeField, Range(2, 24)] int renderDistance = 6;
    [Tooltip("Threads secondaires pour la génération et le maillage (0 = automatique)")]
    [SerializeField, Min(0)] int workerThreads = 0;
    [Tooltip("Nombre max de chunks en cours de génération en même temps")]
    [SerializeField, Min(1)] int maxGenerationsInFlight = 6;
    [Tooltip("Nombre max de maillages en cours en même temps")]
    [SerializeField, Min(1)] int maxMeshJobsInFlight = 6;
    [Tooltip("Temps max par frame (en ms) pour intégrer les chunks terminés. Plus bas = plus fluide, mais chargement plus lent.")]
    [SerializeField, Min(0.5f)] float integrateBudgetMs = 3f;
    [Tooltip("Rayon (en chunks) autour du joueur où les chunks sont visables au raycast (casser / poser). Plus loin, pas de collider.")]
    [SerializeField, Range(1, 6)] int colliderRadius = 2;

    [Header("Génération du monde")]
    [Tooltip("Graine du monde : la même graine donne toujours le même monde")]
    [SerializeField] int worldSeed = 12345;
    [Tooltip("Hauteur moyenne des plaines, en blocs")]
    [SerializeField, Range(8, 160)] int baseHeight = 64;
    [Tooltip("Niveau de la mer : les creux du terrain plus bas que lui se remplissent d'eau (0 = pas d'eau)")]
    [SerializeField, Range(0, 160)] int seaLevel = 62;
    [Tooltip("Hauteur des collines")]
    [SerializeField, Range(0f, 40f)] float hillHeight = 10f;
    [Tooltip("Hauteur des montagnes au-dessus des plaines")]
    [SerializeField, Range(0f, 150f)] float mountainHeight = 80f;
    [Tooltip("Taille des reliefs : 2 = deux fois plus étalés, 0,5 = deux fois plus serrés")]
    [SerializeField, Range(0.25f, 4f)] float featureScale = 1f;
    [SerializeField] bool caves = true;
    [Tooltip("Filons de minerais (charbon, fer, or, diamant) dans la pierre")]
    [SerializeField] bool ores = true;
    [SerializeField, Range(0f, 1f)] float caveDensity = 0.5f;
    [Tooltip("0 = aucun arbre")]
    [SerializeField, Range(0f, 1f)] float treeDensity = 0.5f;
    [Tooltip("Quelques blocs de démonstration (dalle, enclume, torche, tabouret) près de l'origine")]
    [SerializeField] bool showcaseBlocks = true;

    [Header("Sauvegarde")]
    [Tooltip("Écrire les chunks modifiés sur le disque (sinon ils ne survivent qu'à la session)")]
    [SerializeField] bool saveToDisk = true;
    [SerializeField] string saveFileName = "world.save";
    [Tooltip("Sauvegarde automatique toutes les N secondes (0 = seulement à la fermeture)")]
    [SerializeField, Min(0f)] float autosaveInterval = 60f;

    // Trois rayons :
    //  - meshes      : distance <= renderDistance
    //  - données     : distance <= renderDistance + 1  (les 4 voisins d'un chunk dessiné ont toujours leurs données)
    //  - déchargement: distance >  renderDistance + 2  (marge pour éviter charger/décharger en boucle)
    readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
    readonly List<Vector2Int> offsets = new List<Vector2Int>();   // décalages autour du joueur, triés du plus proche au plus loin
    readonly List<Vector2Int> toRemove = new List<Vector2Int>();

    // Chunks MODIFIÉS uniquement (les autres se régénèrent à l'identique).
    // Ce dictionnaire est la vérité : il est rechargé depuis le disque au démarrage.
    readonly Dictionary<Vector2Int, byte[]> savedChunks = new Dictionary<Vector2Int, byte[]>();

    // Version 5 : chaque bloc sauvegardé a aussi son état (niveau de l'eau qui coule)
    const int SaveFormatVersion = 5;

    // --- Threads secondaires ---
    ChunkWorkers workers;
    readonly ConcurrentQueue<GenResult> generatedQueue = new ConcurrentQueue<GenResult>();
    readonly ConcurrentQueue<MeshResult> meshedQueue = new ConcurrentQueue<MeshResult>();
    readonly HashSet<Vector2Int> generating = new HashSet<Vector2Int>();   // générations en cours
    readonly HashSet<Vector2Int> failedChunks = new HashSet<Vector2Int>(); // générations échouées (pas de nouvel essai)
    int meshJobsInFlight;

    sealed class GenResult
    {
        public Vector2Int coord;
        public ChunkData data;   // null = la génération a échoué
    }

    sealed class MeshResult
    {
        public Chunk chunk;
        public int serial;
        public MeshBuffers buffers;
    }

    // Petit cache : presque tous les accès consécutifs tombent dans le même chunk
    Chunk cachedChunk;
    int cachedCx = int.MinValue;
    int cachedCz;

    Vector2Int lastCenter;
    bool saveDirty;       // il y a des changements pas encore écrits sur le disque
    float autosaveTimer;

    string SavePath => Path.Combine(Application.persistentDataPath, saveFileName);

    // ------------------------------------------------------------------
    // Cycle de vie
    // ------------------------------------------------------------------

    void Awake()
    {
        // Les tables de BlockDatabase sont remplies par son constructeur statique, qui lit des ressources Unity
        // (modèles Blockbench). Il DOIT s'exécuter ici, sur le thread principal, avant tout thread secondaire.
        BlockDatabase.Get(BlockType.Air);
        ChunkMesher.SmoothLighting = smoothLighting; // avant le démarrage des threads de maillage
        ApplyBlockAtlas();

        if (player == null && Camera.main != null)
            player = Camera.main.transform;
        if (player == null)
            Debug.LogError("World : assigne le joueur dans le champ Player.", this);
        if (waterMaterial == null)
            Debug.LogWarning("World : aucun matériau d'eau (champ Water Material) : l'eau sera invisible.", this);

        // Décalages (en chunks) dans un disque de rayon renderDistance + 1, triés par distance
        int r = renderDistance + 1;
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        {
            if (dx * dx + dz * dz <= r * r)
                offsets.Add(new Vector2Int(dx, dz));
        }
        offsets.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));

        if (saveToDisk) LoadFromDisk();
    }

    void OnEnable()
    {
        int count = workerThreads > 0 ? workerThreads : Mathf.Clamp(SystemInfo.processorCount - 2, 1, 4);
        workers = new ChunkWorkers(count);
    }

    // Les threads doivent être arrêtés quand le monde disparaît, sinon ils survivent à l'arrêt du mode Play
    void OnDisable()
    {
        if (workers != null)
        {
            workers.Dispose();
            workers = null;
        }
    }

    void Start()
    {
        if (player == null) return;

        lastCenter = GetPlayerChunk();
        UpdateColliderTargets(lastCenter);
    }

    void Update()
    {
        ProcessBlockUpdates();
        ProcessFluids(Time.deltaTime);
        ProcessRedstone(Time.deltaTime);
        TickFurnaces(Time.deltaTime);

        if (player == null || workers == null) return;

        Vector2Int center = GetPlayerChunk();
        if (center != lastCenter)
        {
            lastCenter = center;
            UnloadFar(center);
            UpdateColliderTargets(center);
        }

        // Tout ce qui suit est borné en temps : au pire quelques millisecondes par frame
        var clock = Stopwatch.StartNew();

        SubmitGenerationJobs(center);
        IntegrateGenerated(center, clock);
        SubmitMeshJobs(center);
        IntegrateMeshes(clock);
        ApplyPendingColliders(clock);

        if (saveToDisk && autosaveInterval > 0f)
        {
            autosaveTimer += Time.unscaledDeltaTime;
            if (autosaveTimer >= autosaveInterval)
            {
                autosaveTimer = 0f;
                SaveAll();
            }
        }
    }

    // Appelé aussi quand on quitte le mode Play dans l'éditeur
    void OnApplicationQuit()
    {
        FlushBlockUpdates(); // mises à jour en attente traitées, blocs en vol posés, avant la sauvegarde
        if (saveToDisk) SaveAll();
    }

    Vector2Int GetPlayerChunk()
    {
        return new Vector2Int(
            Mathf.FloorToInt(player.position.x) >> Chunk.BitsXZ,
            Mathf.FloorToInt(player.position.z) >> Chunk.BitsXZ);
    }

    // Vrai si on a déjà passé le temps imparti ET fait au moins une opération (il y a toujours du progrès)
    bool OverBudget(Stopwatch clock, int done)
    {
        return done > 0 && clock.Elapsed.TotalMilliseconds >= integrateBudgetMs;
    }

    // ------------------------------------------------------------------
    // Génération (threads secondaires)
    // ------------------------------------------------------------------

    TerrainSettings CurrentTerrainSettings()
    {
        return new TerrainSettings
        {
            seed = worldSeed,
            baseHeight = baseHeight,
            seaLevel = seaLevel,
            hillHeight = hillHeight,
            mountainHeight = mountainHeight,
            scale = featureScale,
            caves = caves,
            ores = ores,
            caveDensity = caveDensity,
            treeDensity = treeDensity,
            showcase = showcaseBlocks,
        };
    }

    // Lance la génération des chunks manquants, du plus proche au plus loin
    void SubmitGenerationJobs(Vector2Int center)
    {
        TerrainSettings settings = CurrentTerrainSettings();

        foreach (Vector2Int off in offsets)
        {
            if (generating.Count >= maxGenerationsInFlight) break;

            Vector2Int coord = center + off;
            if (chunks.ContainsKey(coord) || generating.Contains(coord) || failedChunks.Contains(coord)) continue;

            generating.Add(coord);

            // Un chunk déjà modifié par le joueur est rechargé tel quel, sinon on le génère
            savedChunks.TryGetValue(coord, out byte[] saved);

            workers.Enqueue(() => GenerateJob(coord, settings, saved));
        }
    }

    // S'exécute sur un thread secondaire : aucune API Unity ici
    void GenerateJob(Vector2Int coord, TerrainSettings settings, byte[] saved)
    {
        ChunkData data = null;

        try
        {
            data = new ChunkData(coord);

            if (saved != null) TerrainGenerator.ImportSaved(data, saved);
            else TerrainGenerator.Generate(data, settings);

            ChunkLighting.ComputeIsolated(data); // lumière du chunk, sans ses voisins
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            data = null;
        }

        generatedQueue.Enqueue(new GenResult { coord = coord, data = data });
    }

    // Reprend les chunks terminés par les threads : création du GameObject (rapide) et échange de lumière
    void IntegrateGenerated(Vector2Int center, Stopwatch clock)
    {
        int done = 0;
        int keepSqr = (renderDistance + 2) * (renderDistance + 2);

        while (!OverBudget(clock, done) && generatedQueue.TryDequeue(out GenResult result))
        {
            generating.Remove(result.coord);

            if (result.data == null) { failedChunks.Add(result.coord); continue; }
            if (chunks.ContainsKey(result.coord)) continue;

            // Le joueur s'est éloigné pendant la génération : on jette le résultat
            Vector2Int d = result.coord - center;
            if (d.x * d.x + d.y * d.y > keepSqr) continue;

            RegisterChunk(result.data);
            done++;
        }
    }

    // Le chunk devient visible du monde (lecture des blocs, collision du joueur...)
    void RegisterChunk(ChunkData data)
    {
        var go = new GameObject($"Chunk {data.coord.x},{data.coord.y}");
        go.transform.SetParent(transform);

        var chunk = go.AddComponent<Chunk>(); // ajoute aussi MeshFilter / MeshRenderer / MeshCollider
        chunk.Init(data, chunkMaterial, waterMaterial, translucentMaterial);
        chunk.WantsCollider = InColliderRadius(data.coord, lastCenter);

        chunks[data.coord] = chunk;
        InvalidateChunkCache();

        // La lumière a été calculée sans les voisins : on l'échange maintenant avec ceux qui sont déjà là
        ExchangeBorders(chunk);

        // Les voisins déjà dessinés dont la lumière vient de changer sont remaillés en arrière-plan
        FlushLightDirtyAsync();
    }

    // ------------------------------------------------------------------
    // Maillage (threads secondaires)
    // ------------------------------------------------------------------

    bool HasAllNeighbors(Vector2Int c)
    {
        return chunks.ContainsKey(c + Vector2Int.right)
            && chunks.ContainsKey(c + Vector2Int.left)
            && chunks.ContainsKey(c + Vector2Int.up)
            && chunks.ContainsKey(c + Vector2Int.down);
    }

    ChunkData DataAt(int cx, int cz)
    {
        Chunk c = GetChunk(cx, cz);
        return ReferenceEquals(c, null) ? null : c.Data;
    }

    // Les données du chunk et de ses 4 voisins : tout ce dont le maillage a besoin
    ChunkSnapshot Snapshot(Chunk chunk)
    {
        Vector2Int c = chunk.Coord;
        return new ChunkSnapshot
        {
            center = chunk.Data,
            px = DataAt(c.x + 1, c.y),
            nx = DataAt(c.x - 1, c.y),
            pz = DataAt(c.x, c.y + 1),
            nz = DataAt(c.x, c.y - 1),
            pxpz = DataAt(c.x + 1, c.y + 1),
            pxnz = DataAt(c.x + 1, c.y - 1),
            nxpz = DataAt(c.x - 1, c.y + 1),
            nxnz = DataAt(c.x - 1, c.y - 1),
        };
    }

    // Lance le maillage des chunks prêts (le chunk ET ses 4 voisins ont leurs données)
    void SubmitMeshJobs(Vector2Int center)
    {
        int radiusSqr = renderDistance * renderDistance;

        foreach (Vector2Int off in offsets)
        {
            if (off.x * off.x + off.y * off.y > radiusSqr) break; // liste triée : tout le reste est trop loin
            if (meshJobsInFlight >= maxMeshJobsInFlight) break;

            Vector2Int coord = center + off;
            if (!chunks.TryGetValue(coord, out Chunk chunk)) continue;
            if (chunk.IsMeshed || chunk.MeshInFlight) continue;
            if (!HasAllNeighbors(coord)) continue;

            QueueMeshJob(chunk);
        }
    }

    void QueueMeshJob(Chunk chunk)
    {
        chunk.MeshInFlight = true;
        chunk.RemeshQueued = false;
        meshJobsInFlight++;

        int serial = ++chunk.BuildSerial;
        ChunkSnapshot snapshot = Snapshot(chunk);
        MeshBuffers buffers = MeshBuffers.Rent();

        workers.Enqueue(() => MeshJob(chunk, snapshot, serial, buffers));
    }

    // S'exécute sur un thread secondaire : aucune API Unity ici (`chunk` n'est que transporté, jamais touché)
    void MeshJob(Chunk chunk, ChunkSnapshot snapshot, int serial, MeshBuffers buffers)
    {
        try
        {
            ChunkMesher.Build(snapshot, buffers);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            buffers.Clear();
            buffers.failed = true;
        }

        meshedQueue.Enqueue(new MeshResult { chunk = chunk, serial = serial, buffers = buffers });
    }

    // Reprend les meshes terminés et les envoie à Unity (budget de temps par frame)
    void IntegrateMeshes(Stopwatch clock)
    {
        int done = 0;

        while (!OverBudget(clock, done) && meshedQueue.TryDequeue(out MeshResult result))
        {
            meshJobsInFlight--;

            Chunk chunk = result.chunk;
            bool alive = chunk != null && chunks.TryGetValue(chunk.Coord, out Chunk current) && current == chunk;

            if (alive)
            {
                chunk.MeshInFlight = false;

                // Un résultat périmé (le chunk a été reconstruit depuis, par exemple après un bloc posé) est ignoré
                if (!result.buffers.failed && chunk.BuildSerial == result.serial)
                {
                    // DIAGNOSTIC (temporaire) : un mesh vide alors que le chunk contient des blocs n'est pas normal
                    if (!emptyMeshReported && result.buffers.vertices.Count == 0 && chunk.Highest >= 0)
                    {
                        emptyMeshReported = true;
                        DiagnoseEmptyMesh(chunk);
                    }

                    chunk.ApplyMesh(result.buffers);
                    done++;
                }

                // Un nouveau maillage a été demandé pendant ce temps (la lumière a changé) : on relance
                if (chunk.RemeshQueued) QueueMeshJob(chunk);
            }

            MeshBuffers.Return(result.buffers);
        }
    }

    // ------------------------------------------------------------------
    // DIAGNOSTIC (temporaire) : à retirer une fois le problème d'affichage résolu
    // ------------------------------------------------------------------

    bool emptyMeshReported;

    // Compare ce que le chunk contient et ce que donne le MÊME maillage refait sur le thread principal
    void DiagnoseEmptyMesh(Chunk chunk)
    {
        ChunkData d = chunk.Data;

        int nonAir = 0, meshable = 0;
        for (int i = 0; i < d.blocks.Length; i++)
        {
            if (d.blocks[i] == 0) continue;
            nonAir++;
            if (BlockDatabase.HasMesh((BlockType)d.blocks[i])) meshable++;
        }

        int syncVertices;
        string syncError = "";
        MeshBuffers b = MeshBuffers.Rent();
        try
        {
            ChunkMesher.Build(Snapshot(chunk), b);
            syncVertices = b.vertices.Count;
        }
        catch (Exception e)
        {
            syncVertices = -1;
            syncError = " ERREUR : " + e.Message;
        }
        finally
        {
            MeshBuffers.Return(b);
        }

        Debug.LogWarning(
            $"[Diagnostic] Chunk {chunk.Coord} : mesh VIDE reçu d'un thread secondaire.\n" +
            $"highest = {d.highest}, blocs non vides = {nonAir}, dont avec un mesh = {meshable}, " +
            $"HasMesh(Grass) = {BlockDatabase.HasMesh(BlockType.Grass)}, HasMesh(Stone) = {BlockDatabase.HasMesh(BlockType.Stone)}.\n" +
            $"Même maillage refait sur le thread principal : {syncVertices} sommets.{syncError}\n" +
            $"Threads : {workers?.ThreadCount}, position du World : {transform.position}",
            chunk);
    }

    // Reconstruit le mesh MAINTENANT, sur le thread principal. Pour les modifications du joueur : le bloc
    // posé ou cassé doit apparaître tout de suite. Annule toute construction en arrière-plan en cours.
    void RebuildNow(Chunk chunk)
    {
        chunk.BuildSerial++;

        MeshBuffers buffers = MeshBuffers.Rent();
        try
        {
            ChunkMesher.Build(Snapshot(chunk), buffers);
            chunk.ApplyMesh(buffers);
        }
        finally
        {
            MeshBuffers.Return(buffers);
        }

        if (chunk.WantsCollider) chunk.ApplyCollider();
    }

    // Demande un nouveau maillage en arrière-plan (la lumière a changé : pas d'urgence)
    void RequestRemesh(Chunk chunk)
    {
        if (chunk.MeshInFlight) { chunk.RemeshQueued = true; return; }
        if (!chunk.IsMeshed) return; // pas encore dessiné : il le sera avec les données à jour

        QueueMeshJob(chunk);
    }

    // ------------------------------------------------------------------
    // Colliders de raycast : seulement près du joueur
    // ------------------------------------------------------------------

    bool InColliderRadius(Vector2Int coord, Vector2Int center)
    {
        Vector2Int d = coord - center;
        return d.x * d.x + d.y * d.y <= colliderRadius * colliderRadius;
    }

    void UpdateColliderTargets(Vector2Int center)
    {
        foreach (var kv in chunks)
        {
            Chunk chunk = kv.Value;
            chunk.WantsCollider = InColliderRadius(kv.Key, center);

            if (!chunk.WantsCollider && chunk.HasCollider) chunk.RemoveCollider();
        }
    }

    // Active les colliders en attente (le plus coûteux : Unity calcule la géométrie de collision)
    void ApplyPendingColliders(Stopwatch clock)
    {
        int done = 0;
        int radiusSqr = colliderRadius * colliderRadius;

        foreach (Vector2Int off in offsets)
        {
            if (off.x * off.x + off.y * off.y > radiusSqr) break;

            Chunk chunk = GetChunk(lastCenter.x + off.x, lastCenter.y + off.y);
            if (ReferenceEquals(chunk, null)) continue;
            if (!chunk.IsMeshed || !chunk.WantsCollider || !chunk.NeedsCollider) continue;

            if (OverBudget(clock, done)) break;
            chunk.ApplyCollider();
            done++;
        }
    }

    // ------------------------------------------------------------------
    // Déchargement
    // ------------------------------------------------------------------

    void UnloadFar(Vector2Int center)
    {
        int unloadRadius = renderDistance + 2;
        int unloadRadiusSqr = unloadRadius * unloadRadius;

        toRemove.Clear();
        foreach (var kv in chunks)
        {
            Vector2Int d = kv.Key - center;
            if (d.x * d.x + d.y * d.y > unloadRadiusSqr)
                toRemove.Add(kv.Key);
        }

        foreach (var key in toRemove)
        {
            SettleFallingBlocksIn(key);

            Chunk chunk = chunks[key];

            // On garde les blocs modifiés avant de détruire le chunk
            if (chunk.IsModified)
            {
                savedChunks[key] = chunk.ExportData();
                saveDirty = true;
            }

            // Une tâche de maillage en cours se terminera quand même : son résultat sera simplement jeté
            Destroy(chunk.gameObject);
            chunks.Remove(key);
        }

        if (toRemove.Count > 0) InvalidateChunkCache();
    }

    // ------------------------------------------------------------------
    // Accès aux chunks et aux blocs (coordonnées MONDE)
    // ------------------------------------------------------------------

    Chunk GetChunk(int cx, int cz)
    {
        if (cx == cachedCx && cz == cachedCz) return cachedChunk;

        chunks.TryGetValue(new Vector2Int(cx, cz), out Chunk chunk);
        cachedCx = cx;
        cachedCz = cz;
        cachedChunk = chunk;
        return chunk;
    }

    void InvalidateChunkCache()
    {
        cachedCx = int.MinValue;
        cachedChunk = null;
    }

    public BlockType GetBlock(int worldX, int worldY, int worldZ)
    {
        if ((uint)worldY >= (uint)Chunk.SizeY) return BlockType.Air;

        // Le décalage de bits arrondit vers le bas, ce qui est exactement ce qu'il faut pour les coordonnées négatives
        Chunk chunk = GetChunk(worldX >> Chunk.BitsXZ, worldZ >> Chunk.BitsXZ);
        if (ReferenceEquals(chunk, null)) return BlockType.Air; // chunk non chargé

        return chunk.GetLocalBlock(worldX & Chunk.MaskXZ, worldY, worldZ & Chunk.MaskXZ);
    }

    // L'atlas est assemblé au lancement à partir des fichiers de blocs : on le donne à des COPIES des matériaux
    // (ton matériau d'origine n'est jamais modifié), avec le nombre de tuiles et leur taille.
    void ApplyBlockAtlas()
    {
        if (chunkMaterial != null)
        {
            chunkMaterial = new Material(chunkMaterial) { name = chunkMaterial.name + " (atlas)" };
            SetTextureIf(chunkMaterial, "_BaseMap", BlockDatabase.Atlas);
            SetTextureIf(chunkMaterial, "_HeightMap", BlockDatabase.HeightAtlas);
            SetTextureIf(chunkMaterial, "_MetallicGlossMap", BlockDatabase.MetallicAtlas);
            SetTextureIf(chunkMaterial, "_EmissionMap", BlockDatabase.EmissionAtlas);
            SetFloatIf(chunkMaterial, "_AtlasTiles", BlockDatabase.AtlasTilesPerRow);
            SetFloatIf(chunkMaterial, "_PixelsPerBlock", BlockDatabase.TilePixels);
        }
        if (chunkMaterial != null)
        {
            translucentMaterial = new Material(chunkMaterial) { name = chunkMaterial.name + " (translucide)" };
            translucentMaterial.SetFloat("_Translucent", 1f);
            translucentMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            translucentMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            translucentMaterial.SetFloat("_ZWrite", 0f);
            translucentMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        if (waterMaterial != null)
        {
            waterMaterial = new Material(waterMaterial) { name = waterMaterial.name + " (atlas)" };
            SetTextureIf(waterMaterial, "_BaseMap", BlockDatabase.Atlas);
            SetFloatIf(waterMaterial, "_AtlasTiles", BlockDatabase.AtlasTilesPerRow);
            SetFloatIf(waterMaterial, "_PixelsPerTile", BlockDatabase.TilePixels);
        }
    }

    static void SetTextureIf(Material m, string property, Texture texture)
    {
        if (texture != null && m.HasProperty(property)) m.SetTexture(property, texture);
    }

    static void SetFloatIf(Material m, string property, float value)
    {
        if (m.HasProperty(property)) m.SetFloat(property, value);
    }

    // Hauteur où l'on peut se tenir debout dans la colonne (x, z) : dessus du plus haut bloc solide.
    // Les feuilles et l'herbe haute (sans collision) sont ignorées. Le chunk doit être chargé.
    public float GetSurfaceY(int worldX, int worldZ)
    {
        for (int y = Chunk.SizeY - 1; y >= 0; y--)
        {
            BlockType type = GetBlock(worldX, y, worldZ);

            // De l'eau avant le sol : on apparaît à sa surface (et on nage)
            if (BlockDatabase.GetRef(type).shape == BlockShape.Liquid) return y + 1f;

            float top = BlockDatabase.SupportHeight(type);
            if (top > 0f) return y + top;
        }
        return 0f;
    }

    // Pose un bloc avec son ÉTAT (répéteur : sens et délai). Remaille tout de suite, comme SetBlock.
    public bool PlaceBlockWithState(int worldX, int worldY, int worldZ, BlockType type, byte state)
    {
        return SetBlockAndState(worldX, worldY, worldZ, type, state, false);
    }

    // État d'un bloc à une position MONDE (niveau de l'eau...). 0 si le chunk n'est pas chargé.
    public byte GetState(int worldX, int worldY, int worldZ)
    {
        if ((uint)worldY >= (uint)Chunk.SizeY) return 0;

        Chunk chunk = GetChunk(worldX >> Chunk.BitsXZ, worldZ >> Chunk.BitsXZ);
        if (ReferenceEquals(chunk, null)) return 0;

        return chunk.GetLocalState(worldX & Chunk.MaskXZ, worldY, worldZ & Chunk.MaskXZ);
    }

    // Modifie un bloc, met à jour la lumière, reconstruit les meshes concernés et marque le chunk à sauvegarder.
    // Retourne false si rien n'a changé (hors monde, chunk non chargé, même bloc).
    public bool SetBlock(int worldX, int worldY, int worldZ, BlockType type)
    {
        return SetBlockAndState(worldX, worldY, worldZ, type, 0, false);
    }

    // fromFluid = changement fait par l'écoulement de l'eau : beaucoup de blocs changent à la fois, donc les
    // meshes sont refaits en arrière-plan, une seule fois à la fin de la mise à jour de l'eau (FlushFluidChanges).
    bool SetBlockAndState(int worldX, int worldY, int worldZ, BlockType type, byte blockState, bool fromFluid)
    {
        if ((uint)worldY >= (uint)Chunk.SizeY) return false;

        int cx = worldX >> Chunk.BitsXZ;
        int cz = worldZ >> Chunk.BitsXZ;

        Chunk chunk = GetChunk(cx, cz);
        if (ReferenceEquals(chunk, null)) return false;

        int lx = worldX & Chunk.MaskXZ;
        int lz = worldZ & Chunk.MaskXZ;

        BlockType old = chunk.GetLocalBlock(lx, worldY, lz);
        if (old == type && chunk.GetLocalState(lx, worldY, lz) == blockState) return false;

        chunk.SetLocalBlock(lx, worldY, lz, type, blockState);
        if (chunk.Highest > maxHighestLoaded) maxHighestLoaded = chunk.Highest;

        // Lumière : seul le TYPE du bloc compte (le niveau de l'eau ne change pas la lumière)
        if (old != type) UpdateLightAt(worldX, worldY, worldZ);

        if (fromFluid)
        {
            MarkFluidDirty(chunk.Coord, lx, lz);
        }
        else
        {
            // Chunks à reconstruire tout de suite : celui du bloc, et ses voisins si le bloc est sur une bordure.
            // Ceux dont seule la lumière a changé sont remaillés en arrière-plan.
            MarkDirty(cx, cz);
            if (lx == 0)               MarkDirty(cx - 1, cz);
            if (lx == Chunk.SizeX - 1) MarkDirty(cx + 1, cz);
            if (lz == 0)               MarkDirty(cx, cz - 1);
            if (lz == Chunk.SizeZ - 1) MarkDirty(cx, cz + 1);
            FlushDirtyChunks();
        }

        if (old != type) QueueNeighborUpdates(worldX, worldY, worldZ);

        // La redstone autour (et ici) doit être recalculée
        if (redstoneEnabled) NotifyRedstone(worldX, worldY, worldZ);

        // L'eau autour (et ici) doit peut-être couler, reculer ou se remplir
        ScheduleFluidAround(worldX, worldY, worldZ);

        return true;
    }

    // ------------------------------------------------------------------
    // Sauvegarde
    // ------------------------------------------------------------------

    // Copie les chunks chargés et modifiés dans savedChunks, puis écrit le fichier si nécessaire
    void SaveAll()
    {
        SaveFurnaces();

        foreach (var kv in chunks)
        {
            if (!kv.Value.IsModified) continue;
            savedChunks[kv.Key] = kv.Value.ExportData();
            kv.Value.ClearModified();
            saveDirty = true;
        }

        if (saveToDisk && saveDirty)
            WriteToDisk();
    }

    void WriteToDisk()
    {
        try
        {
            string tmp = SavePath + ".tmp";

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(SaveFormatVersion);
                w.Write(Chunk.SizeX);
                w.Write(Chunk.SizeY);
                w.Write(Chunk.SizeZ);
                w.Write(savedChunks.Count);

                foreach (var kv in savedChunks)
                {
                    w.Write(kv.Key.x);
                    w.Write(kv.Key.y);
                    w.Write(kv.Value); // tableau de taille fixe (Chunk.SaveLength : types puis états)
                }
            }

            // On écrit dans un fichier temporaire puis on le déplace : un crash en cours d'écriture
            // ne détruit pas la sauvegarde précédente.
            if (File.Exists(SavePath)) File.Delete(SavePath);
            File.Move(tmp, SavePath);

            saveDirty = false;
        }
        catch (Exception e)
        {
            Debug.LogError($"World : échec de l'écriture de la sauvegarde ({e.Message})", this);
        }
    }

    void LoadFromDisk()
    {
        if (!File.Exists(SavePath)) return;

        try
        {
            using (var fs = new FileStream(SavePath, FileMode.Open, FileAccess.Read))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                int version = r.ReadInt32();
                int sx = r.ReadInt32(), sy = r.ReadInt32(), sz = r.ReadInt32();

                if (version != SaveFormatVersion || sx != Chunk.SizeX || sy != Chunk.SizeY || sz != Chunk.SizeZ)
                {
                    Debug.LogWarning("World : sauvegarde d'un format différent (ancienne hauteur de chunk ?), ignorée.", this);
                    return;
                }

                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int x = r.ReadInt32();
                    int z = r.ReadInt32();
                    byte[] data = r.ReadBytes(Chunk.SaveLength);
                    if (data.Length != Chunk.SaveLength) break; // fichier tronqué

                    savedChunks[new Vector2Int(x, z)] = data;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"World : sauvegarde illisible, elle est ignorée ({e.Message})", this);
            savedChunks.Clear();
        }
    }

    // Clic droit sur le composant World dans l'Inspector pour repartir d'un monde vierge
    [ContextMenu("Supprimer la sauvegarde")]
    void DeleteSave()
    {
        savedChunks.Clear();
        saveDirty = false;
        if (File.Exists(SavePath)) File.Delete(SavePath);
        if (File.Exists(FurnacePath)) File.Delete(FurnacePath);
        furnaces.Clear();
        Debug.Log($"World : sauvegarde supprimée ({SavePath})");
    }
}
