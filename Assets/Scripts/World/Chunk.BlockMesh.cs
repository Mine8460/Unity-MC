using System.Collections.Generic;
using UnityEngine;

// Construction du mesh d'un bloc seul (entités).
// (Partie de la classe Chunk : ajoute le mot "partial" à sa déclaration.)
public partial class Chunk
{
    // Mesh d'un bloc seul (toutes les faces, sans voisins) : pour les entités, comme les blocs qui tombent.
    // Les sommets sont en coordonnées de bloc (0..1) : place le GameObject à l'angle du bloc.
    public static Mesh BuildBlockMesh(BlockType type)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        BlockInfo info = BlockDatabase.Get(type);
        switch (info.shape)
        {
            case BlockShape.Cross:
            {
                const float h = 0.318f;
                ChunkMesher.AddPlane(verts, normals, uvs, tris, new Vector3(0.5f - h, 0f, 0.5f - h), new Vector3(0.5f + h, 0f, 0.5f + h), info.tileSide);
                ChunkMesher.AddPlane(verts, normals, uvs, tris, new Vector3(0.5f + h, 0f, 0.5f - h), new Vector3(0.5f - h, 0f, 0.5f + h), info.tileSide);
                break;
            }

            case BlockShape.Model:
                if (info.elements != null)
                {
                    foreach (Element el in info.elements)
                        for (int f = 0; f < 6; f++)
                            if (el.tiles[f] >= 0)
                                ChunkMesher.AddBoxFace(verts, tris, normals, uvs, Vector3.zero, el.min, el.max, f, el.tiles[f]);
                }

                if (info.quads != null)
                {
                    foreach (ModelQuad q in info.quads)
                    {
                        int start = verts.Count;
                        verts.Add(q.p0); verts.Add(q.p1); verts.Add(q.p2); verts.Add(q.p3);
                        uvs.Add(q.uv0);  uvs.Add(q.uv1);  uvs.Add(q.uv2);  uvs.Add(q.uv3);
                        for (int i = 0; i < 4; i++) normals.Add(q.normal);

                        tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                        tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
                    }
                }
                break;

            default: // Cube
                for (int f = 0; f < 6; f++)
                    ChunkMesher.AddBoxFace(verts, tris, normals, uvs, Vector3.zero, Vector3.zero, Vector3.one, f, BlockDatabase.GetTile(type, f));
                break;
        }

        var mesh = new Mesh { name = "Block " + type };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        // Pleine lumière du ciel, aucune lumière de torche : le bloc en chute n'a pas de case où lire sa lumière.
        // (alpha 0 = éclairage normal, jamais en "plein éclat")
        var colors = new Color32[verts.Count];
        for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(0, 255, 255, 0);
        mesh.colors32 = colors;
        mesh.RecalculateBounds();
        return mesh;
    }

    // Décalage d'une plante en croix à une position MONDE (utilisé par le contour du bloc visé).
    // Garde la MÊME amplitude que PlantOffset.
    public static Vector3 PlantOffsetAt(int worldX, int worldZ)
    {
        return ChunkMesher.PlantOffsetAt(worldX, worldZ);
    }
}
