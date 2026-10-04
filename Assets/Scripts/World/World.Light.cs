using System;
using System.Collections.Generic;
using UnityEngine;

// Lumière par bloc : deux canaux de 0 à 15 (torches et ciel), propagés dans le monde.
// Chaque pas perd 1 niveau ; les blocs opaques bloquent tout ; les feuilles filtrent 1 niveau de plus.
// Le ciel descend sans perte tant que rien ne le filtre.
//
// Qui fait quoi :
//   - la lumière À L'INTÉRIEUR d'un chunk est calculée sur un thread secondaire (ChunkLighting) ;
//   - ici (thread principal) : l'échange de lumière entre un nouveau chunk et ses voisins, et les mises à jour
//     quand le joueur casse ou pose un bloc.
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

    // Chunks dont la FORME a changé (bloc posé/cassé) : remaillés tout de suite
    readonly HashSet<Vector2Int> geometryDirty = new HashSet<Vector2Int>();

    // Chunks dont seule la LUMIÈRE a changé : remaillés en arrière-plan
    readonly HashSet<Vector2Int> lightDirty = new HashSet<Vector2Int>();

    // Files réutilisées pour l'échange de lumière à l'enregistrement d'un chunk
    readonly Queue<Vector3Int> exchangeSkyQueue = new Queue<Vector3Int>();
    readonly Queue<Vector3Int> exchangeBlockQueue = new Queue<Vector3Int>();

    // Hauteur du plus haut bloc de tous les chunks chargés (borne supérieure, ne fait que monter)
    int maxHighestLoaded = -1;

    void MarkDirty(int cx, int cz) => geometryDirty.Add(new Vector2Int(cx, cz));

    // La lumière d'une case a changé : on remaille son chunk et, si la case est contre une frontière, le chunk d'à côté
    // (ses faces de bordure lisent la lumière de CETTE case pour s'éclairer).
    void MarkLightDirty(Vector2Int coord, int lx, int lz)
    {
        lightDirty.Add(coord);

        if (lx == 0) lightDirty.Add(new Vector2Int(coord.x - 1, coord.y));
        else if (lx == Chunk.SizeX - 1) lightDirty.Add(new Vector2Int(coord.x + 1, coord.y));

        if (lz == 0) lightDirty.Add(new Vector2Int(coord.x, coord.y - 1));
        else if (lz == Chunk.SizeZ - 1) lightDirty.Add(new Vector2Int(coord.x, coord.y + 1));
    }

    // Après une modification de bloc : reconstruit tout de suite la forme, remaille la lumière en arrière-plan
    void FlushDirtyChunks()
    {
        foreach (Vector2Int c in geometryDirty)
        {
            Chunk chunk = GetChunk(c.x, c.y);
            if (ReferenceEquals(chunk, null)) continue;

            if (chunk.IsMeshed) RebuildNow(chunk);
            else RequestRemesh(chunk); // pas encore dessiné : si une tâche tourne, elle sera refaite
        }

        foreach (Vector2Int c in lightDirty)
        {
            if (geometryDirty.Contains(c)) continue; // déjà reconstruit à l'instant

            Chunk chunk = GetChunk(c.x, c.y);
            if (!ReferenceEquals(chunk, null)) RequestRemesh(chunk);
        }

        geometryDirty.Clear();
        lightDirty.Clear();
    }

    // À l'enregistrement d'un chunk : seule la lumière a changé chez les voisins, tout peut se faire en arrière-plan
    void FlushLightDirtyAsync()
    {
        foreach (Vector2Int c in lightDirty)
        {
            Chunk chunk = GetChunk(c.x, c.y);
            if (!ReferenceEquals(chunk, null)) RequestRemesh(chunk);
        }

        lightDirty.Clear();
    }

    // ------------------------------------------------------------------
    // Accès
    // ------------------------------------------------------------------

    // Lumière des torches à une position MONDE (0 à 15)
    public int GetBlockLight(int x, int y, int z)
    {
        if ((uint)y >= (uint)Chunk.SizeY) return 0;

        Chunk chunk = GetChunk(x >> Chunk.BitsXZ, z >> Chunk.BitsXZ);
        if (ReferenceEquals(chunk, null)) return 0;

        return chunk.GetBlockLight(x & Chunk.MaskXZ, y, z & Chunk.MaskXZ);
    }

    // Lumière du ciel à une position MONDE (0 à 15)
    public int GetSkyLight(int x, int y, int z)
    {
        if (y >= Chunk.SizeY) return MaxLight; // au-dessus du monde : ciel ouvert
        if (y < 0) return 0;

        Chunk chunk = GetChunk(x >> Chunk.BitsXZ, z >> Chunk.BitsXZ);
        if (ReferenceEquals(chunk, null)) return 0;

        return chunk.GetSkyLight(x & Chunk.MaskXZ, y, z & Chunk.MaskXZ);
    }

    int GetLevel(int x, int y, int z, bool sky) => sky ? GetSkyLight(x, y, z) : GetBlockLight(x, y, z);

    void SetLevel(int x, int y, int z, int level, bool sky)
    {
        if ((uint)y >= (uint)Chunk.SizeY) return;

        Chunk chunk = GetChunk(x >> Chunk.BitsXZ, z >> Chunk.BitsXZ);
        if (ReferenceEquals(chunk, null)) return;

        int lx = x & Chunk.MaskXZ;
        int lz = z & Chunk.MaskXZ;

        int current = sky ? chunk.GetSkyLight(lx, y, lz) : chunk.GetBlockLight(lx, y, lz);
        if (current == level) return;

        if (sky) chunk.SetSkyLight(lx, y, lz, level);
        else chunk.SetBlockLight(lx, y, lz, level);

        MarkLightDirty(chunk.Coord, lx, lz);
    }

    // ------------------------------------------------------------------
    // Propagation
    // ------------------------------------------------------------------

    // Étend la lumière à partir des cases de la file, à travers tous les chunks chargés
    void SpreadLight(Queue<Vector3Int> queue, bool sky)
    {
        while (queue.Count > 0)
        {
            Vector3Int p = queue.Dequeue();
            if ((uint)p.y >= (uint)Chunk.SizeY) continue;

            ChunkData pd = DataAt(p.x >> Chunk.BitsXZ, p.z >> Chunk.BitsXZ);
            if (pd == null) continue;

            int pi = ChunkData.Index(p.x & Chunk.MaskXZ, p.y, p.z & Chunk.MaskXZ);
            int level = sky ? pd.light[pi] >> 4 : pd.light[pi] & 15;
            if (level <= 1) continue;

            for (int d = 0; d < 6; d++)
            {
                Vector3Int n = p + LightDirs[d];
                if ((uint)n.y >= (uint)Chunk.SizeY) continue;

                ChunkData nd = DataAt(n.x >> Chunk.BitsXZ, n.z >> Chunk.BitsXZ);
                if (nd == null) continue;

                int ni = ChunkData.Index(n.x & Chunk.MaskXZ, n.y, n.z & Chunk.MaskXZ);
                int opacity = BlockDatabase.LightOpacity((BlockType)nd.blocks[ni]);
                if (opacity >= MaxLight) continue;

                // Le ciel à 15 descend sans perte à travers l'air
                int next = (sky && d == DownDir && level == MaxLight && opacity == 0)
                    ? MaxLight
                    : level - Mathf.Max(1, opacity);

                int current = sky ? nd.light[ni] >> 4 : nd.light[ni] & 15;
                if (next <= current) continue;

                if (sky) nd.light[ni] = (byte)((nd.light[ni] & 0x0F) | (next << 4));
                else nd.light[ni] = (byte)((nd.light[ni] & 0xF0) | next);

                MarkLightDirty(nd.coord, n.x & Chunk.MaskXZ, n.z & Chunk.MaskXZ);
                queue.Enqueue(n);
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
                if ((uint)n.y >= (uint)Chunk.SizeY || ReferenceEquals(GetChunk(n.x >> Chunk.BitsXZ, n.z >> Chunk.BitsXZ), null)) continue;

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
    // Échange entre un nouveau chunk et ses voisins
    // ------------------------------------------------------------------

    // Le thread secondaire a calculé la lumière du chunk SANS ses voisins. Ici on regarde, le long de chaque
    // frontière, où la lumière d'un côté peut éclairer l'autre, puis on la propage.
    void ExchangeBorders(Chunk chunk)
    {
        ChunkData a = chunk.Data;
        if (a.highest > maxHighestLoaded) maxHighestLoaded = a.highest;

        // Au-dessus du plus haut bloc de tout le monde chargé, il n'y a que du ciel ouvert (15 partout)
        // et plus de lumière de torche (elle perd 1 niveau par case) : rien à échanger.
        int ymax = Mathf.Min(Chunk.SizeY - 1, maxHighestLoaded + MaxLight);

        exchangeSkyQueue.Clear();
        exchangeBlockQueue.Clear();

        Vector2Int c = a.coord;
        ExchangeSide(a, DataAt(c.x + 1, c.y), 0, ymax);   // voisin en +X
        ExchangeSide(a, DataAt(c.x - 1, c.y), 1, ymax);   // voisin en -X
        ExchangeSide(a, DataAt(c.x, c.y + 1), 4, ymax);   // voisin en +Z
        ExchangeSide(a, DataAt(c.x, c.y - 1), 5, ymax);   // voisin en -Z

        SpreadLight(exchangeSkyQueue, true);
        SpreadLight(exchangeBlockQueue, false);
    }

    // dir = direction de a vers b : 0 = +X, 1 = -X, 4 = +Z, 5 = -Z (indices de LightDirs)
    void ExchangeSide(ChunkData a, ChunkData b, int dir, int ymax)
    {
        if (b == null) return;

        int aOx = a.coord.x << Chunk.BitsXZ, aOz = a.coord.y << Chunk.BitsXZ;
        int bOx = b.coord.x << Chunk.BitsXZ, bOz = b.coord.y << Chunk.BitsXZ;

        for (int k = 0; k < Chunk.SizeX; k++)
        {
            // Case de a le long de la frontière, et case voisine dans b (coordonnées locales)
            int ax, az, bx, bz;
            switch (dir)
            {
                case 0:  ax = Chunk.SizeX - 1; az = k; bx = 0;                bz = k; break;
                case 1:  ax = 0;               az = k; bx = Chunk.SizeX - 1;  bz = k; break;
                case 4:  ax = k; az = Chunk.SizeZ - 1; bx = k; bz = 0;               break;
                default: ax = k; az = 0;               bx = k; bz = Chunk.SizeZ - 1; break;
            }

            int ai = ChunkData.Index(ax, 0, az);
            int bi = ChunkData.Index(bx, 0, bz);

            for (int y = 0; y <= ymax; y++)
            {
                int la = a.light[ai + y], lb = b.light[bi + y];
                int opA = BlockDatabase.LightOpacity((BlockType)a.blocks[ai + y]);
                int opB = BlockDatabase.LightOpacity((BlockType)b.blocks[bi + y]);

                // Ciel : a éclaire-t-elle b ? b éclaire-t-elle a ?
                if (CanImprove(la >> 4, opB, dir, true, lb >> 4))
                    exchangeSkyQueue.Enqueue(new Vector3Int(aOx + ax, y, aOz + az));
                if (CanImprove(lb >> 4, opA, dir ^ 1, true, la >> 4))
                    exchangeSkyQueue.Enqueue(new Vector3Int(bOx + bx, y, bOz + bz));

                // Torches
                if (CanImprove(la & 15, opB, dir, false, lb & 15))
                    exchangeBlockQueue.Enqueue(new Vector3Int(aOx + ax, y, aOz + az));
                if (CanImprove(lb & 15, opA, dir ^ 1, false, la & 15))
                    exchangeBlockQueue.Enqueue(new Vector3Int(bOx + bx, y, bOz + bz));
            }
        }
    }

    // Une case de niveau `level` peut-elle améliorer sa voisine (d'opacité `targetOpacity`, de niveau `targetLevel`) ?
    static bool CanImprove(int level, int targetOpacity, int dir, bool sky, int targetLevel)
    {
        if (level <= 1 || targetOpacity >= MaxLight) return false;

        int next = (sky && dir == DownDir && level == MaxLight && targetOpacity == 0)
            ? MaxLight
            : level - Mathf.Max(1, targetOpacity);

        return next > targetLevel;
    }

    // ------------------------------------------------------------------
    // Modifications de blocs
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
                if ((uint)n.y >= (uint)Chunk.SizeY || ReferenceEquals(GetChunk(n.x >> Chunk.BitsXZ, n.z >> Chunk.BitsXZ), null)) continue;
                if (GetLevel(n.x, n.y, n.z, sky) > 1) addQueue.Enqueue(n);
            }
        }

        // 3) La lumière se propage
        SpreadLight(addQueue, sky);
    }
}
