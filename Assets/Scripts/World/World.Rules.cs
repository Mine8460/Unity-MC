using System.Collections.Generic;
using UnityEngine;

// Règles de blocs du monde : support, gravité, blocs qui tombent.
// (Partie de la classe World : ajoute le mot "partial" à sa déclaration.)
public partial class World
{
    [Header("Mises à jour de blocs")]
    [Tooltip("Nombre max de blocs revérifiés par frame (support, gravité)")]
    [SerializeField, Min(1)] int maxBlockUpdatesPerFrame = 256;

    readonly Queue<Vector3Int> pendingUpdates = new();
    readonly HashSet<Vector3Int> queuedUpdates = new();
    readonly List<FallingBlock> fallingBlocks = new();
    readonly Dictionary<BlockType, Mesh> fallingMeshes = new();

    // ------------------------------------------------------------------
    // Règles de blocs : support, gravité, pose
    // ------------------------------------------------------------------

    void OnDestroy()
    {
        foreach (Mesh m in fallingMeshes.Values)
            if (m != null) Destroy(m);
    }

    // Peut-on poser ce bloc ici ? (case libre ou remplaçable, et support valide)
    public bool CanPlace(int x, int y, int z, BlockType type, byte state = 0)
    {
        if (y < 0 || y >= Chunk.SizeY) return false;
        if (!BlockDatabase.Get(GetBlock(x, y, z)).replaceable) return false;
        return HasSupport(x, y, z, BlockDatabase.Get(type), state);
    }

    // À utiliser à la place de SetBlock quand le JOUEUR pose un bloc
    public bool TryPlaceBlock(int x, int y, int z, BlockType type)
    {
        return CanPlace(x, y, z, type) && SetBlock(x, y, z, type);
    }

    // Le support exigé par le bloc existe-t-il sous lui ?
    bool HasSupport(int x, int y, int z, BlockInfo info, byte state = 0)
    {
        BlockInfo below = BlockDatabase.Get(GetBlock(x, y - 1, z));

        switch (info.support)
        {
            case SupportRule.SolidBelow: return below.shape == BlockShape.Cube && below.collidable;
            case SupportRule.SoilBelow:  return below.isSoil;
            case SupportRule.OpaqueBelow: return below.opaque; // cube plein et opaque (le verre ne convient pas)
            case SupportRule.SolidAttached:
            {
                Vector3Int d = info.attachDir;

                // Bloc tourné (torche murale) : la direction du mur tourne avec lui
                if (info.orientation != Orientation.None && state != 0)
                {
                    Vector3 r = BlockOrientation.RotateDir(info.orientation, BlockOrientation.Clamp(info.orientation, state), new Vector3(d.x, d.y, d.z));
                    d = new Vector3Int(Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y), Mathf.RoundToInt(r.z));
                }
                BlockInfo wall = BlockDatabase.Get(GetBlock(x + d.x, y + d.y, z + d.z));
                return wall.shape == BlockShape.Cube && wall.collidable;
            }
            default:                     return true;
        }
    }

    // La colonne (x, z) est-elle dans un chunk chargé ?
    public bool IsLoaded(int worldX, int worldZ)
    {
        int cx = Mathf.FloorToInt(worldX / (float)Chunk.SizeX);
        int cz = Mathf.FloorToInt(worldZ / (float)Chunk.SizeZ);
        return chunks.ContainsKey(new Vector2Int(cx, cz));
    }

    // Quand un bloc change, lui et ses 6 voisins sont revérifiés (support, gravité).
    // Les vérifications passent par une file : une chaîne de blocs qui tombent ou se cassent
    // se déroule sur plusieurs frames, sans récursion.
    void QueueUpdate(int x, int y, int z)
    {
        var p = new Vector3Int(x, y, z);
        if (queuedUpdates.Add(p)) pendingUpdates.Enqueue(p);
    }

    void QueueNeighborUpdates(int x, int y, int z)
    {
        QueueUpdate(x, y, z);
        QueueUpdate(x + 1, y, z);
        QueueUpdate(x - 1, y, z);
        QueueUpdate(x, y + 1, z);
        QueueUpdate(x, y - 1, z);
        QueueUpdate(x, y, z + 1);
        QueueUpdate(x, y, z - 1);
    }

    void ProcessBlockUpdates()
    {
        int budget = maxBlockUpdatesPerFrame;
        while (budget-- > 0 && pendingUpdates.Count > 0)
        {
            Vector3Int p = pendingUpdates.Dequeue();
            queuedUpdates.Remove(p);
            UpdateBlock(p.x, p.y, p.z);
        }
    }

    void UpdateBlock(int x, int y, int z)
    {
        BlockType type = GetBlock(x, y, z);
        BlockInfo info = BlockDatabase.Get(type);
        if (!info.hasMesh) return;

        // 1) Le support a disparu : le bloc se casse et lâche son objet (la torche), ou rien (l'herbe haute)
        if (info.support != SupportRule.None && !HasSupport(x, y, z, info, info.orientation != Orientation.None ? GetState(x, y, z) : (byte)0))
        {
            BreakBlock(x, y, z);
            return;
        }

        // 2) Bloc soumis à la gravité, sans appui : il devient une entité qui tombe
        if (info.gravity && y > 0 && BlockDatabase.Get(GetBlock(x, y - 1, z)).replaceable)
            StartFalling(x, y, z, type);
    }

    // Le bloc quitte la grille et devient une entité
    void StartFalling(int x, int y, int z, BlockType type)
    {
        SetBlock(x, y, z, BlockType.Air); // prévient aussi les voisins (le bloc du dessus peut tomber à son tour)

        var go = new GameObject("Falling " + type);
        go.transform.SetParent(transform);
        go.transform.position = new Vector3(x, y, z);

        go.AddComponent<MeshFilter>().sharedMesh = GetFallingMesh(type);
        go.AddComponent<MeshRenderer>().sharedMaterials = new[] { chunkMaterial, TranslucentMaterial };

        var fb = go.AddComponent<FallingBlock>();
        fb.Init(this, type, new Vector3Int(x, y, z));
        fallingBlocks.Add(fb);
    }

    // Un mesh par type de bloc, partagé entre toutes les entités
    Mesh GetFallingMesh(BlockType type)
    {
        if (!fallingMeshes.TryGetValue(type, out Mesh mesh))
        {
            mesh = Chunk.BuildBlockMesh(type);
            fallingMeshes[type] = mesh;
        }
        return mesh;
    }

    public void UnregisterFalling(FallingBlock fb)
    {
        fallingBlocks.Remove(fb);
    }

    // Avant de décharger un chunk : ses blocs en train de tomber sont posés au sol, pour ne pas être perdus
    void SettleFallingBlocksIn(Vector2Int chunkCoord)
    {
        for (int i = fallingBlocks.Count - 1; i >= 0; i--)
        {
            if (fallingBlocks[i].ChunkCoord == chunkCoord)
                fallingBlocks[i].SettleNow();
        }
    }

    void SettleAllFallingBlocks()
    {
        for (int i = fallingBlocks.Count - 1; i >= 0; i--)
            fallingBlocks[i].SettleNow();
    }

    // Fermeture du jeu : toutes les mises à jour en attente sont traitées et les blocs en vol sont posés,
    // pour qu'aucune enclume ne reste suspendue dans la sauvegarde
    void FlushBlockUpdates()
    {
        for (int i = 0; i < 100000 && (pendingUpdates.Count > 0 || fallingBlocks.Count > 0); i++)
        {
            ProcessBlockUpdates();
            SettleAllFallingBlocks();
        }
    }
}
