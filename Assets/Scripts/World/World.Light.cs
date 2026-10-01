using System.Collections.Generic;
using UnityEngine;

// Lumière par bloc : deux canaux de 0 à 15 (torches et ciel), propagés dans le monde.
// Chaque pas perd 1 niveau ; les blocs opaques bloquent tout ; les feuilles filtrent 1 niveau de plus.
// Le ciel descend sans perte tant que rien ne le filtre.
public partial class World
{
    const int MaxLight = 15;
    const int DownDir = 3; // index de "vers le bas" dans LightDirs

    static readonly Vector3Int[] LightDirs =
    {
        new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
        new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
    };

    // Chunks dont la lumière ou la forme a changé : reconstruits une seule fois chacun
    readonly HashSet<Vector2Int> dirtyChunks = new();

    void MarkDirty(int cx, int cz) => dirtyChunks.Add(new Vector2Int(cx, cz));

    void FlushDirtyChunks()
    {
        if (dirtyChunks.Count == 0) return;

        foreach (Vector2Int c in dirtyChunks)
            RebuildIfMeshed(c.x, c.y); // ne fait rien si le chunk n'a pas encore de mesh

        dirtyChunks.Clear();
    }

    // ------------------------------------------------------------------
    // Accès
    // ------------------------------------------------------------------

    bool TryGetChunkAt(int worldX, int worldZ, out Chunk chunk, out int lx, out int lz)
    {
        int cx = Mathf.FloorToInt(worldX / (float)Chunk.SizeX);
        int cz = Mathf.FloorToInt(worldZ / (float)Chunk.SizeZ);
        lx = worldX - cx * Chunk.SizeX;
        lz = worldZ - cz * Chunk.SizeZ;
        return chunks.TryGetValue(new Vector2Int(cx, cz), out chunk);
    }

    // Lumière des torches à une position MONDE (0 à 15)
    public int GetBlockLight(int x, int y, int z)
    {
        if (y < 0 || y >= Chunk.SizeY) return 0;
        return TryGetChunkAt(x, z, out Chunk chunk, out int lx, out int lz) ? chunk.GetBlockLight(lx, y, lz) : 0;
    }

    // Lumière du ciel à une position MONDE (0 à 15)
    public int GetSkyLight(int x, int y, int z)
    {
        if (y >= Chunk.SizeY) return MaxLight; // au-dessus du monde : ciel ouvert
        if (y < 0) return 0;
        return TryGetChunkAt(x, z, out Chunk chunk, out int lx, out int lz) ? chunk.GetSkyLight(lx, y, lz) : 0;
    }

    int GetLevel(int x, int y, int z, bool sky) => sky ? GetSkyLight(x, y, z) : GetBlockLight(x, y, z);

    void SetLevel(int x, int y, int z, int level, bool sky)
    {
        if (y < 0 || y >= Chunk.SizeY) return;
        if (!TryGetChunkAt(x, z, out Chunk chunk, out int lx, out int lz)) return;

        int current = sky ? chunk.GetSkyLight(lx, y, lz) : chunk.GetBlockLight(lx, y, lz);
        if (current == level) return;

        if (sky) chunk.SetSkyLight(lx, y, lz, level);
        else chunk.SetBlockLight(lx, y, lz, level);

        dirtyChunks.Add(chunk.Coord);
    }

    // ------------------------------------------------------------------
    // Propagation
    // ------------------------------------------------------------------

    // Étend la lumière à partir des cases de la file
    void SpreadLight(Queue<Vector3Int> queue, bool sky)
    {
        while (queue.Count > 0)
        {
            Vector3Int p = queue.Dequeue();
            int level = GetLevel(p.x, p.y, p.z, sky);
            if (level <= 1) continue;

            for (int d = 0; d < 6; d++)
            {
                Vector3Int n = p + LightDirs[d];
                if (n.y < 0 || n.y >= Chunk.SizeY || !IsLoaded(n.x, n.z)) continue;

                int opacity = BlockDatabase.LightOpacity(GetBlock(n.x, n.y, n.z));
                if (opacity >= MaxLight) continue;

                // Le ciel à 15 descend sans perte à travers l'air
                int next = (sky && d == DownDir && level == MaxLight && opacity == 0)
                    ? MaxLight
                    : level - Mathf.Max(1, opacity);

                if (next > GetLevel(n.x, n.y, n.z, sky))
                {
                    SetLevel(n.x, n.y, n.z, next, sky);
                    queue.Enqueue(n);
                }
            }
        }
    }

    // Retire la lumière qui dépendait des cases de la file. Les cases qui ont une autre source
    // sont ajoutées à addQueue pour être re-propagées ensuite.
    void RemoveLight(Queue<(Vector3Int pos, int level)> removeQueue, Queue<Vector3Int> addQueue, bool sky)
    {
        while (removeQueue.Count > 0)
        {
            var (p, level) = removeQueue.Dequeue();

            for (int d = 0; d < 6; d++)
            {
                Vector3Int n = p + LightDirs[d];
                if (n.y < 0 || n.y >= Chunk.SizeY || !IsLoaded(n.x, n.z)) continue;

                int neighborLevel = GetLevel(n.x, n.y, n.z, sky);
                if (neighborLevel == 0) continue;

                // Le voisin tirait-il sa lumière de p ? (le ciel qui descend à 15 en dépend toujours)
                bool derived = neighborLevel < level
                               || (sky && d == DownDir && level == MaxLight && neighborLevel == MaxLight);

                if (derived)
                {
                    SetLevel(n.x, n.y, n.z, 0, sky);
                    removeQueue.Enqueue((n, neighborLevel));
                }
                else
                {
                    addQueue.Enqueue(n);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Mises à jour
    // ------------------------------------------------------------------

    // Appelé par SetBlock après le changement d'un bloc : met à jour la lumière autour de lui
    void UpdateLightAt(int x, int y, int z)
    {
        RelightCell(x, y, z, false);
        RelightCell(x, y, z, true);
    }

    void RelightCell(int x, int y, int z, bool sky)
    {
        var removeQueue = new Queue<(Vector3Int pos, int level)>();
        var addQueue = new Queue<Vector3Int>();
        var pos = new Vector3Int(x, y, z);

        BlockType type = GetBlock(x, y, z);
        int opacity = BlockDatabase.LightOpacity(type);

        // 1) L'ancienne lumière de la case, et celle qui en dépendait, disparaissent
        int old = GetLevel(x, y, z, sky);
        if (old > 0)
        {
            SetLevel(x, y, z, 0, sky);
            removeQueue.Enqueue((pos, old));
            RemoveLight(removeQueue, addQueue, sky);
        }

        // 2) Ce que la case émet ou reçoit maintenant
        if (opacity < MaxLight)
        {
            int emission = sky ? 0 : BlockDatabase.Emission(type);
            if (emission > 0)
            {
                SetLevel(x, y, z, emission, sky);
                addQueue.Enqueue(pos);
            }

            // Tout en haut du monde, le ciel entre directement par le dessus
            if (sky && y == Chunk.SizeY - 1)
            {
                int top = opacity == 0 ? MaxLight : MaxLight - Mathf.Max(1, opacity);
                if (top > GetLevel(x, y, z, sky))
                {
                    SetLevel(x, y, z, top, sky);
                    addQueue.Enqueue(pos);
                }
            }

            // Sinon, elle se remplit depuis ses voisins éclairés
            for (int d = 0; d < 6; d++)
            {
                Vector3Int n = pos + LightDirs[d];
                if (n.y < 0 || n.y >= Chunk.SizeY || !IsLoaded(n.x, n.z)) continue;
                if (GetLevel(n.x, n.y, n.z, sky) > 1) addQueue.Enqueue(n);
            }
        }

        // 3) La lumière se propage
        SpreadLight(addQueue, sky);
    }

    // Calcule la lumière d'un chunk qui vient d'être créé (blocs déjà en place) et l'échange avec ses voisins.
    // À appeler une fois le chunk ajouté à `chunks`.
    void InitChunkLight(Chunk chunk)
    {
        int ox = chunk.Coord.x * Chunk.SizeX;
        int oz = chunk.Coord.y * Chunk.SizeZ;

        var skyQueue = new Queue<Vector3Int>();
        var blockQueue = new Queue<Vector3Int>();

        for (int x = 0; x < Chunk.SizeX; x++)
        for (int z = 0; z < Chunk.SizeZ; z++)
        {
            int sky = MaxLight; // le ciel arrive par le haut de la colonne

            for (int y = Chunk.SizeY - 1; y >= 0; y--)
            {
                BlockType type = chunk.GetLocalBlock(x, y, z);
                int opacity = BlockDatabase.LightOpacity(type);

                if (opacity >= MaxLight) sky = 0;
                else if (!(sky == MaxLight && opacity == 0)) sky = Mathf.Max(0, sky - Mathf.Max(1, opacity));

                var pos = new Vector3Int(ox + x, y, oz + z);

                if (opacity < MaxLight && sky > 0)
                {
                    chunk.SetSkyLight(x, y, z, sky);
                    if (sky > 1) skyQueue.Enqueue(pos);
                }

                int emission = BlockDatabase.Emission(type);
                if (emission > 0)
                {
                    chunk.SetBlockLight(x, y, z, emission);
                    blockQueue.Enqueue(pos);
                }
            }
        }

        // La lumière des chunks voisins déjà chargés entre dans celui-ci
        void Seed(int wx, int wy, int wz)
        {
            if (!IsLoaded(wx, wz)) return;
            var p = new Vector3Int(wx, wy, wz);
            skyQueue.Enqueue(p);
            blockQueue.Enqueue(p);
        }

        for (int y = 0; y < Chunk.SizeY; y++)
        {
            for (int k = 0; k < Chunk.SizeZ; k++)
            {
                Seed(ox + Chunk.SizeX, y, oz + k);
                Seed(ox - 1, y, oz + k);
            }
            for (int k = 0; k < Chunk.SizeX; k++)
            {
                Seed(ox + k, y, oz + Chunk.SizeZ);
                Seed(ox + k, y, oz - 1);
            }
        }

        SpreadLight(skyQueue, true);
        SpreadLight(blockQueue, false);

        // Les voisins déjà dessinés dont la lumière vient de changer sont reconstruits
        FlushDirtyChunks();
    }
}
