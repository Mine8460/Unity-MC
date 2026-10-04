using System;
using System.Collections.Generic;

// Calcul de la lumière d'UN chunk, pris isolément : le ciel qui descend, les torches, et leur propagation
// À L'INTÉRIEUR du chunk. Thread-safe : ne lit et n'écrit que les tableaux du ChunkData reçu.
//
// L'échange avec les chunks voisins (la lumière qui passe d'un chunk à l'autre) est fait ensuite
// par le thread principal, au moment où le chunk est enregistré (World.ExchangeBorders).
public static class ChunkLighting
{
    const int Max = 15;
    const int DownDir = 3;   // index de "vers le bas" dans les tableaux ci-dessous

    // Mêmes directions et même ordre que World.LightDirs : +X, -X, +Y, -Y, +Z, -Z
    static readonly int[] DX = { 1, -1, 0, 0, 0, 0 };
    static readonly int[] DY = { 0, 0, 1, -1, 0, 0 };
    static readonly int[] DZ = { 0, 0, 0, 0, 1, -1 };

    // Files réutilisées par chaque thread : aucune allocation à chaque chunk
    [ThreadStatic] static Queue<int> skyQueue;
    [ThreadStatic] static Queue<int> blockQueue;

    public static void ComputeIsolated(ChunkData d)
    {
        if (skyQueue == null) skyQueue = new Queue<int>(4096);
        if (blockQueue == null) blockQueue = new Queue<int>(256);
        skyQueue.Clear();
        blockQueue.Clear();

        byte[] blocks = d.blocks;
        byte[] light = d.light;

        // Au-dessus du plus haut bloc, c'est du ciel ouvert partout : rien à calculer
        int top = Math.Min(d.highest + 1, Chunk.SizeY - 1);

        // 1) Colonne par colonne : le ciel descend sans perte à travers l'air, et on repère les torches
        for (int x = 0; x < Chunk.SizeX; x++)
        for (int z = 0; z < Chunk.SizeZ; z++)
        {
            int col = ChunkData.Index(x, 0, z);

            for (int y = Chunk.SizeY - 1; y > top; y--)
                light[col + y] = 0xF0; // ciel 15, torche 0

            int sky = Max;
            for (int y = top; y >= 0; y--)
            {
                BlockType type = (BlockType)blocks[col + y];
                int opacity = BlockDatabase.LightOpacity(type);

                if (opacity >= Max) sky = 0;
                else if (!(sky == Max && opacity == 0)) sky = Math.Max(0, sky - Math.Max(1, opacity));

                if (opacity < Max && sky > 0)
                    light[col + y] = (byte)((light[col + y] & 0x0F) | (sky << 4));

                int emission = BlockDatabase.Emission(type);
                if (emission > 0)
                {
                    light[col + y] = (byte)((light[col + y] & 0xF0) | emission);
                    blockQueue.Enqueue(col + y);
                }
            }
        }

        // 2) Graines du ciel : seulement les cases qui peuvent éclairer une voisine plus sombre
        for (int x = 0; x < Chunk.SizeX; x++)
        for (int z = 0; z < Chunk.SizeZ; z++)
        {
            int col = ChunkData.Index(x, 0, z);
            for (int y = 0; y <= top; y++)
            {
                int level = light[col + y] >> 4;
                if (level > 1 && CanImprove(blocks, light, x, y, z, level, true))
                    skyQueue.Enqueue(col + y);
            }
        }

        // 3) Propagation à l'intérieur du chunk
        Spread(blocks, light, skyQueue, true);
        Spread(blocks, light, blockQueue, false);
    }

    // Cette case peut-elle améliorer au moins une voisine DU MÊME CHUNK ?
    static bool CanImprove(byte[] blocks, byte[] light, int x, int y, int z, int level, bool sky)
    {
        for (int dir = 0; dir < 6; dir++)
        {
            int nx = x + DX[dir], ny = y + DY[dir], nz = z + DZ[dir];
            if ((uint)nx >= Chunk.SizeX || (uint)ny >= Chunk.SizeY || (uint)nz >= Chunk.SizeZ) continue;

            int n = ChunkData.Index(nx, ny, nz);
            int opacity = BlockDatabase.LightOpacity((BlockType)blocks[n]);
            if (opacity >= Max) continue;

            int next = (sky && dir == DownDir && level == Max && opacity == 0) ? Max : level - Math.Max(1, opacity);
            int current = sky ? light[n] >> 4 : light[n] & 15;
            if (next > current) return true;
        }

        return false;
    }

    static void Spread(byte[] blocks, byte[] light, Queue<int> queue, bool sky)
    {
        int sizeY = Chunk.SizeY;
        int sizeZ = Chunk.SizeZ;

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int level = sky ? light[idx] >> 4 : light[idx] & 15;
            if (level <= 1) continue;

            // Retrouve (x, y, z) d'après l'indice
            int y = idx % sizeY;
            int xz = idx / sizeY;
            int z = xz % sizeZ;
            int x = xz / sizeZ;

            for (int dir = 0; dir < 6; dir++)
            {
                int nx = x + DX[dir], ny = y + DY[dir], nz = z + DZ[dir];
                if ((uint)nx >= Chunk.SizeX || (uint)ny >= (uint)sizeY || (uint)nz >= (uint)sizeZ) continue;

                int n = (nx * sizeZ + nz) * sizeY + ny;
                int opacity = BlockDatabase.LightOpacity((BlockType)blocks[n]);
                if (opacity >= Max) continue;

                int next = (sky && dir == DownDir && level == Max && opacity == 0) ? Max : level - Math.Max(1, opacity);
                int current = sky ? light[n] >> 4 : light[n] & 15;
                if (next <= current) continue;

                if (sky) light[n] = (byte)((light[n] & 0x0F) | (next << 4));
                else light[n] = (byte)((light[n] & 0xF0) | next);

                queue.Enqueue(n);
            }
        }
    }
}
