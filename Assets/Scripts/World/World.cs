using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

public partial class World : MonoBehaviour
{
    [Header("Références")]
    [SerializeField] Material chunkMaterial;
    [SerializeField] Transform player;

    [Header("Chargement des chunks")]
    [Tooltip("Rayon de rendu, en chunks (1 chunk = 16 blocs)")]
    [SerializeField, Range(2, 24)] int renderDistance = 6;
    [Tooltip("Nombre max de chunks dont on génère les données par frame")]
    [SerializeField, Min(1)] int maxDataPerFrame = 4;
    [Tooltip("Nombre max de meshes construits par frame (le plus coûteux)")]
    [SerializeField, Min(1)] int maxMeshPerFrame = 2;

    [Header("Terrain")]
    [SerializeField, Range(1, Chunk.SizeY)] int terrainHeight = 8;

    [Header("Sauvegarde")]
    [Tooltip("Écrire les chunks modifiés sur le disque (sinon ils ne survivent qu'à la session)")]
    [SerializeField] bool saveToDisk = true;
    [SerializeField] string saveFileName = "world.save";
    [Tooltip("Sauvegarde automatique toutes les N secondes (0 = seulement à la fermeture)")]
    [SerializeField, Min(0f)] float autosaveInterval = 60f;

    // Trois rayons :
    //  - meshes      : distance <= renderDistance
    //  - données     : distance <= renderDistance + 1  (les 4 voisins d'un chunk meshé ont toujours leurs données)
    //  - déchargement: distance >  renderDistance + 2  (marge pour éviter charger/décharger en boucle)
    readonly Dictionary<Vector2Int, Chunk> chunks = new();
    readonly List<Vector2Int> offsets = new();   // décalages autour du joueur, triés du plus proche au plus loin
    readonly List<Vector2Int> toRemove = new();

    // Chunks MODIFIÉS uniquement (les autres se régénèrent à l'identique).
    // Ce dictionnaire est la vérité : il est rechargé depuis le disque au démarrage.
    readonly Dictionary<Vector2Int, byte[]> savedChunks = new();

    const int SaveFormatVersion = 1;

    Vector2Int lastCenter;
    bool pending;
    bool saveDirty;       // il y a des changements pas encore écrits sur le disque
    float autosaveTimer;

    string SavePath => Path.Combine(Application.persistentDataPath, saveFileName);

    void Awake()
    {
        if (player == null && Camera.main != null)
            player = Camera.main.transform;
        if (player == null)
            Debug.LogError("World : assigne le joueur dans le champ Player.", this);

        // Décalages (en chunks) dans un disque de rayon renderDistance + 1, triés par distance
        int r = renderDistance + 1;
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (dx * dx + dz * dz <= r * r)
                    offsets.Add(new Vector2Int(dx, dz));
            }
        offsets.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));

        // Doit être fait AVANT le premier chargement de chunks (Start)
        if (saveToDisk) LoadFromDisk();
    }

    void Start()
    {
        if (player == null) return;

        // Chargement initial complet et immédiat : le sol existe avant la première frame du joueur
        lastCenter = GetPlayerChunk();
        Process(lastCenter, int.MaxValue, int.MaxValue);
    }

    void Update()
    {
        if (player == null) return;

        ProcessBlockUpdates();

        Vector2Int center = GetPlayerChunk();
        if (center != lastCenter)
        {
            lastCenter = center;
            UnloadFar(center);
            pending = true;
        }

        // Le travail est étalé sur plusieurs frames pour éviter les saccades
        if (pending)
            pending = Process(center, maxDataPerFrame, maxMeshPerFrame);

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
        FlushBlockUpdates();
        if (saveToDisk) SaveAll();
    }

    Vector2Int GetPlayerChunk()
    {
        return new Vector2Int(
            Mathf.FloorToInt(player.position.x / Chunk.SizeX),
            Mathf.FloorToInt(player.position.z / Chunk.SizeZ));
    }

    // Retourne true s'il reste du travail à faire (budget épuisé ou voisins pas prêts)
    bool Process(Vector2Int center, int dataBudget, int meshBudget)
    {
        bool workLeft = false;

        // 1) Données : du plus proche au plus loin
        foreach (var off in offsets)
        {
            var coord = center + off;
            if (chunks.ContainsKey(coord)) continue;

            if (dataBudget <= 0) { workLeft = true; break; }
            CreateChunk(coord);
            dataBudget--;
        }

        // 2) Meshes : seulement dans le rayon de rendu, et si les 4 voisins ont leurs données
        int meshRadiusSqr = renderDistance * renderDistance;
        foreach (var off in offsets)
        {
            if (off.x * off.x + off.y * off.y > meshRadiusSqr) break; // liste triée : tout le reste est trop loin

            var coord = center + off;
            if (!chunks.TryGetValue(coord, out Chunk chunk)) { workLeft = true; continue; }
            if (chunk.IsMeshed) continue;
            if (!HasAllNeighbors(coord)) { workLeft = true; continue; }

            if (meshBudget <= 0) { workLeft = true; break; }
            chunk.BuildMesh();
            meshBudget--;
        }

        return workLeft;
    }

    void CreateChunk(Vector2Int coord)
    {
        var go = new GameObject($"Chunk {coord.x},{coord.y}");
        go.transform.SetParent(transform);

        var chunk = go.AddComponent<Chunk>(); // ajoute aussi MeshFilter/Renderer/Collider
        chunk.Init(this, coord, chunkMaterial);

        // Un chunk déjà modifié par le joueur est rechargé tel quel, sinon on le génère
        if (savedChunks.TryGetValue(coord, out byte[] saved))
            chunk.ImportData(saved);
        else
            chunk.GenerateData(terrainHeight);

        chunks[coord] = chunk;
        InitChunkLight(chunk);
    }

    bool HasAllNeighbors(Vector2Int c)
    {
        return chunks.ContainsKey(c + Vector2Int.right)
            && chunks.ContainsKey(c + Vector2Int.left)
            && chunks.ContainsKey(c + Vector2Int.up)
            && chunks.ContainsKey(c + Vector2Int.down);
    }

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

            Destroy(chunk.gameObject);
            chunks.Remove(key);
        }
    }

    // ------------------------------------------------------------------
    // Accès aux blocs (coordonnées MONDE)
    // ------------------------------------------------------------------

    public BlockType GetBlock(int worldX, int worldY, int worldZ)
    {
        if (worldY < 0 || worldY >= Chunk.SizeY) return BlockType.Air;

        // FloorToInt gère correctement les coordonnées négatives
        int cx = Mathf.FloorToInt(worldX / (float)Chunk.SizeX);
        int cz = Mathf.FloorToInt(worldZ / (float)Chunk.SizeZ);

        if (!chunks.TryGetValue(new Vector2Int(cx, cz), out Chunk chunk))
            return BlockType.Air; // chunk non chargé

        int lx = worldX - cx * Chunk.SizeX;
        int lz = worldZ - cz * Chunk.SizeZ;
        return chunk.GetLocalBlock(lx, worldY, lz);
    }

    // Modifie un bloc, reconstruit les meshes concernés et marque le chunk à sauvegarder.
    // Retourne false si rien n'a changé (hors monde, chunk non chargé, même bloc).
    public bool SetBlock(int worldX, int worldY, int worldZ, BlockType type)
    {
        if (worldY < 0 || worldY >= Chunk.SizeY) return false;

        int cx = Mathf.FloorToInt(worldX / (float)Chunk.SizeX);
        int cz = Mathf.FloorToInt(worldZ / (float)Chunk.SizeZ);

        if (!chunks.TryGetValue(new Vector2Int(cx, cz), out Chunk chunk))
            return false;

        int lx = worldX - cx * Chunk.SizeX;
        int lz = worldZ - cz * Chunk.SizeZ;

        if (chunk.GetLocalBlock(lx, worldY, lz) == type) return false;

        chunk.SetLocalBlock(lx, worldY, lz, type);

        // Lumière : met à jour les torches et le ciel autour du bloc (marque les chunks dont la lumière change)
        UpdateLightAt(worldX, worldY, worldZ);

        // Chunks à reconstruire : celui du bloc, ses voisins si le bloc est sur une bordure,
        // et ceux dont la lumière a changé. Chacun n'est reconstruit qu'une seule fois.
        MarkDirty(cx, cz);
        if (lx == 0)                MarkDirty(cx - 1, cz);
        if (lx == Chunk.SizeX - 1)  MarkDirty(cx + 1, cz);
        if (lz == 0)                MarkDirty(cx, cz - 1);
        if (lz == Chunk.SizeZ - 1)  MarkDirty(cx, cz + 1);
        FlushDirtyChunks();

        QueueNeighborUpdates(worldX, worldY, worldZ);

        return true;
    }

    // Un chunk pas encore meshé prendra la modification en compte quand il le sera
    void RebuildIfMeshed(int cx, int cz)
    {
        if (chunks.TryGetValue(new Vector2Int(cx, cz), out Chunk c) && c.IsMeshed)
            c.BuildMesh();
    }

    // ------------------------------------------------------------------
    // Sauvegarde
    // ------------------------------------------------------------------

    // Copie les chunks chargés et modifiés dans savedChunks, puis écrit le fichier si nécessaire
    void SaveAll()
    {
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
                    w.Write(kv.Value); // tableau de taille fixe (Chunk.DataLength)
                }
            }

            // On écrit dans un fichier temporaire puis on le déplace : un crash en cours d'écriture
            // ne détruit pas la sauvegarde précédente.
            if (File.Exists(SavePath)) File.Delete(SavePath);
            File.Move(tmp, SavePath);

            saveDirty = false;
        }
        catch (System.Exception e)
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
                    Debug.LogWarning("World : sauvegarde d'un format différent, ignorée.", this);
                    return;
                }

                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int x = r.ReadInt32();
                    int z = r.ReadInt32();
                    byte[] data = r.ReadBytes(Chunk.DataLength);
                    if (data.Length != Chunk.DataLength) break; // fichier tronqué

                    savedChunks[new Vector2Int(x, z)] = data;
                }
            }
        }
        catch (System.Exception e)
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
        Debug.Log($"World : sauvegarde supprimée ({SavePath})");
    }

    public void PlaceBlock(int worldX, int worldY, int worldZ, BlockType type)
    {
        if (type == BlockType.Bedrock)
            return; // ne peut pas placer de bedrock

        SetBlock(worldX, worldY, worldZ, type);
    }
}