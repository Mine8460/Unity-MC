using UnityEngine;

// Les DONNÉES d'un chunk : blocs et lumière. Classe simple, sans rien d'Unity (pas de GameObject ni de Mesh),
// donc utilisable depuis les threads de génération.
//
// Règle d'or : un ChunkData qui vient d'être créé n'appartient qu'à son thread. Une fois remis au thread
// principal (World.RegisterChunk), c'est LUI qui le modifie ; les threads de maillage ne font que le LIRE.
public sealed class ChunkData
{
    public readonly Vector2Int coord;

    // 1 octet par bloc (un BlockType). Rangement : les cases d'une même colonne sont côte à côte (y varie le plus vite).
    public readonly byte[] blocks = new byte[Chunk.DataLength];

    // 1 octet par bloc : bits 0-3 = lumière des torches, bits 4-7 = lumière du ciel (0 à 15 chacune)
    public readonly byte[] light = new byte[Chunk.DataLength];

    // 1 octet d'ÉTAT par bloc, propre à chaque type. Pour l'eau : 0 = source, 1 à 7 = eau qui coule (distance
    // à la source), 8 = eau qui tombe (voir WaterState). Remis à 0 quand le type du bloc change.
    public readonly byte[] state = new byte[Chunk.DataLength];

    // Hauteur du plus haut bloc non vide. Borne SUPÉRIEURE (elle peut être trop haute après une casse) :
    // au-dessus, il n'y a que de l'air, donc on n'a rien à parcourir.
    public int highest = -1;

    public ChunkData(Vector2Int coord)
    {
        this.coord = coord;
    }

    public static int Index(int x, int y, int z)
    {
        return (x * Chunk.SizeZ + z) * Chunk.SizeY + y;
    }

    public BlockType GetBlock(int x, int y, int z)
    {
        return (BlockType)blocks[Index(x, y, z)];
    }

    public void SetBlock(int x, int y, int z, BlockType type)
    {
        SetBlock(x, y, z, type, 0);
    }

    public void SetBlock(int x, int y, int z, BlockType type, byte blockState)
    {
        int i = Index(x, y, z);
        blocks[i] = (byte)type;
        state[i] = blockState;
        if (type != BlockType.Air && y > highest) highest = y;
    }

    public byte GetState(int x, int y, int z)
    {
        return state[Index(x, y, z)];
    }

    public int GetBlockLight(int x, int y, int z)
    {
        return light[Index(x, y, z)] & 15;
    }

    public int GetSkyLight(int x, int y, int z)
    {
        return light[Index(x, y, z)] >> 4;
    }

    public void SetBlockLight(int x, int y, int z, int level)
    {
        int i = Index(x, y, z);
        light[i] = (byte)((light[i] & 0xF0) | level);
    }

    public void SetSkyLight(int x, int y, int z, int level)
    {
        int i = Index(x, y, z);
        light[i] = (byte)((light[i] & 0x0F) | (level << 4));
    }

    // Recalcule la hauteur max exacte (après avoir cassé des blocs en haut du chunk)
    public void RecomputeHighest()
    {
        for (int y = highest; y >= 0; y--)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                if (blocks[Index(x, y, z)] != 0)
                {
                    highest = y;
                    return;
                }
            }
        }

        highest = -1;
    }
}
