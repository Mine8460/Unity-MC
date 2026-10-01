using System.Collections.Generic;
using UnityEngine;

// Lumière stockée par chunk. Elle est recalculée par World au chargement du chunk et n'est jamais sauvegardée.
// (Partie de la classe Chunk : ajoute le mot "partial" à sa déclaration, c'est déjà fait si tu as suivi les étapes précédentes.)
public partial class Chunk
{
    // 1 octet par bloc : bits 0-3 = lumière des torches, bits 4-7 = lumière du ciel (0 à 15 chacune)
    readonly byte[] light = new byte[SizeX * SizeY * SizeZ];

    static int LightIndex(int x, int y, int z) => (x * SizeY + y) * SizeZ + z;

    public int GetBlockLight(int x, int y, int z) => light[LightIndex(x, y, z)] & 15;
    public int GetSkyLight(int x, int y, int z) => light[LightIndex(x, y, z)] >> 4;

    public void SetBlockLight(int x, int y, int z, int level)
    {
        int i = LightIndex(x, y, z);
        light[i] = (byte)((light[i] & 0xF0) | level);
    }

    public void SetSkyLight(int x, int y, int z, int level)
    {
        int i = LightIndex(x, y, z);
        light[i] = (byte)((light[i] & 0x0F) | (level << 4));
    }

    // Écrit la lumière dans les couleurs de sommet : R = lumière des torches, G = lumière du ciel
    // (0 à 255 pour les niveaux 0 à 15), B = 255. L'alpha (mode d'éclairage) est conservé.
    // Un échantillon par quad (4 sommets consécutifs), pris juste DEVANT la face.
    void ApplyVertexLight(List<Vector3> vertices, List<Vector3> normals, Color32[] colors)
    {
        int ox = Coord.x * SizeX;
        int oz = Coord.y * SizeZ;

        for (int i = 0; i + 3 < vertices.Count; i += 4)
        {
            Vector3 center = (vertices[i] + vertices[i + 1] + vertices[i + 2] + vertices[i + 3]) * 0.25f;
            Vector3 p = center + normals[i] * 0.25f;

            int wx = ox + Mathf.FloorToInt(p.x);
            int wy = Mathf.FloorToInt(p.y);
            int wz = oz + Mathf.FloorToInt(p.z);

            byte blockLight = (byte)(world.GetBlockLight(wx, wy, wz) * 17);
            byte skyLight = (byte)(world.GetSkyLight(wx, wy, wz) * 17);

            for (int k = 0; k < 4; k++)
                colors[i + k] = new Color32(blockLight, skyLight, 255, colors[i + k].a);
        }
    }
}
