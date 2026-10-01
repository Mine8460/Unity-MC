using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 16;
    public const int SizeZ = 16;

    // Taille des données d'un chunk une fois sérialisé (1 octet par bloc)
    public const int DataLength = SizeX * SizeY * SizeZ;

    public Vector2Int Coord { get; private set; }
    public bool IsMeshed { get; private set; }

    // true si un bloc a été changé depuis la génération / le dernier enregistrement
    public bool IsModified { get; private set; }

    World world;
    readonly BlockType[,,] blocks = new BlockType[SizeX, SizeY, SizeZ];
    Mesh mesh;          // ce qu'on VOIT
    Mesh colliderMesh;  // ce qui est SOLIDE (sans les feuilles, l'herbe, etc.)
    MeshFilter meshFilter;
    MeshCollider meshCollider;

    static readonly Element[] NoElements = new Element[0];

    // Direction de chaque face : haut, bas, +Z, -Z, +X, -X
    static readonly Vector3Int[] Dirs =
    {
        new(0, 1, 0), new(0, -1, 0), new(0, 0, 1),
        new(0, 0, -1), new(1, 0, 0), new(-1, 0, 0)
    };

    // 4 sommets par face du cube unité, sens horaire vu de l'extérieur.
    // Pour une boîte partielle, chaque coordonnée 0/1 est remplacée par min/max.
    static readonly Vector3[][] FaceVerts =
    {
        new[] { new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(1,1,1), new Vector3(1,1,0) }, // haut
        new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(0,0,1) }, // bas
        new[] { new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1), new Vector3(0,0,1) }, // +Z
        new[] { new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,0,0) }, // -Z
        new[] { new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(1,0,1) }, // +X
        new[] { new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0), new Vector3(0,0,0) }, // -X
    };

    public void Init(World world, Vector2Int coord, Material material)
    {
        this.world = world;
        Coord = coord;

        transform.position = new Vector3(coord.x * SizeX, 0, coord.y * SizeZ);

        meshFilter = GetComponent<MeshFilter>();
        meshCollider = GetComponent<MeshCollider>();
        GetComponent<MeshRenderer>().sharedMaterial = material;

        mesh = new Mesh { name = $"Chunk {coord.x},{coord.y}" };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        colliderMesh = new Mesh { name = $"Chunk Collider {coord.x},{coord.y}" };
        colliderMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    }

    // ------------------------------------------------------------------
    // Génération
    // ------------------------------------------------------------------

    // Étape 1 : remplir les données
    public void GenerateData(int terrainHeight, bool placeTree = true)
    {
        for (int x = 0; x < SizeX; x++)
            for (int z = 0; z < SizeZ; z++)
                for (int y = 0; y < SizeY; y++)
                {
                    BlockType type;
                    if (y >= terrainHeight) type = BlockType.Air;
                    else if (y == terrainHeight - 1) type = BlockType.Grass;
                    else if (y >= terrainHeight - 3) type = BlockType.Dirt;
                    else if (y == 0) type = BlockType.Bedrock;
                    else type = BlockType.Stone;

                    blocks[x, y, z] = type;
                }

        if (placeTree)
            PlaceTree(SizeX / 2, SizeZ / 2, terrainHeight);

        // Herbes hautes sur environ 1 case d'herbe sur 5 (jamais à la place d'un tronc ou de feuilles)
        if (terrainHeight < SizeY)
        {
            for (int x = 0; x < SizeX; x++)
                for (int z = 0; z < SizeZ; z++)
                {
                    int wx = Coord.x * SizeX + x;
                    int wz = Coord.y * SizeZ + z;
                    if (Hash01(wx, wz, 0) < 0.2f)
                        TrySet(x, terrainHeight, z, BlockType.TallGrass);
                }
        }

        // Quelques blocs de démonstration près de l'origine
        if (Coord == Vector2Int.zero)
            PlaceShowcase(terrainHeight);
    }

    // Une rangée de blocs à modèle custom pour les voir tout de suite
    void PlaceShowcase(int groundY)
    {
        Force(3, groundY, 3, BlockType.StoneSlab);
        Force(5, groundY, 3, BlockType.Anvil);
        Force(7, groundY, 3, BlockType.AnvilRotated);
        Force(9, groundY, 3, BlockType.Torch);
    }

    // Petit arbre de test : tronc + boule de feuilles (reste dans les limites du chunk)
    void PlaceTree(int tx, int tz, int groundY)
    {
        const int trunkHeight = 4;

        for (int i = 0; i < trunkHeight; i++)
            TrySet(tx, groundY + i, tz, BlockType.Log);

        int cy = groundY + trunkHeight;
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 1; dy++)
                for (int dz = -2; dz <= 2; dz++)
                {
                    if (dx * dx + dy * dy + dz * dz <= 5)
                        TrySet(tx + dx, cy + dy, tz + dz, BlockType.Leaves);
                }
    }

    // Place un bloc seulement s'il est dans le chunk et que la case est vide
    void TrySet(int x, int y, int z, BlockType type)
    {
        if (x < 0 || x >= SizeX || y < 0 || y >= SizeY || z < 0 || z >= SizeZ) return;
        if (blocks[x, y, z] != BlockType.Air) return;
        blocks[x, y, z] = type;
    }

    // Place un bloc dans le chunk, même si la case est occupée
    void Force(int x, int y, int z, BlockType type)
    {
        if (x < 0 || x >= SizeX || y < 0 || y >= SizeY || z < 0 || z >= SizeZ) return;
        blocks[x, y, z] = type;
    }

    // Nombre pseudo-aléatoire stable entre 0 et 1 pour une position (x, z)
    static float Hash01(int x, int z, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + salt * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / (float)0x1000000;
        }
    }

    // ------------------------------------------------------------------
    // Accès aux blocs / sauvegarde
    // ------------------------------------------------------------------

    // Modifie un bloc (coordonnées locales) et marque le chunk comme modifié.
    // Ne reconstruit PAS le mesh : c'est World.SetBlock qui s'en charge.
    public void SetLocalBlock(int x, int y, int z, BlockType type)
    {
        blocks[x, y, z] = type;
        IsModified = true;
    }

    public void ClearModified() => IsModified = false;

    // Sérialise les blocs (1 octet par bloc)
    public byte[] ExportData()
    {
        var data = new byte[DataLength];
        int i = 0;
        for (int x = 0; x < SizeX; x++)
            for (int y = 0; y < SizeY; y++)
                for (int z = 0; z < SizeZ; z++)
                    data[i++] = (byte)blocks[x, y, z];
        return data;
    }

    // Recharge les blocs depuis des données sérialisées (à la place de GenerateData)
    public void ImportData(byte[] data)
    {
        if (data == null || data.Length != DataLength)
        {
            Debug.LogWarning($"Chunk {Coord} : données de sauvegarde invalides, ignorées.");
            return;
        }

        int i = 0;
        for (int x = 0; x < SizeX; x++)
            for (int y = 0; y < SizeY; y++)
                for (int z = 0; z < SizeZ; z++)
                    blocks[x, y, z] = (BlockType)data[i++];
    }

    // Les Mesh créés par code ne sont pas libérés automatiquement : on les détruit avec le chunk
    void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (colliderMesh != null) Destroy(colliderMesh);
    }

    // Lecture directe dans CE chunk (coordonnées locales valides uniquement)
    public BlockType GetLocalBlock(int x, int y, int z) => blocks[x, y, z];

    // Lecture avec coordonnées locales. Hors du chunk => on demande au World.
    BlockType GetBlock(int x, int y, int z)
    {
        if (y < 0 || y >= SizeY) return BlockType.Air;

        if (x >= 0 && x < SizeX && z >= 0 && z < SizeZ)
            return blocks[x, y, z];

        return world.GetBlock(Coord.x * SizeX + x, y, Coord.y * SizeZ + z);
    }

    BlockType GetNeighbor(int x, int y, int z, int face)
    {
        Vector3Int n = new Vector3Int(x, y, z) + Dirs[face];
        return GetBlock(n.x, n.y, n.z);
    }

    // ------------------------------------------------------------------
    // Construction des meshes
    // ------------------------------------------------------------------

    // Étape 2 : construire les meshes (quand TOUS les chunks ont leurs données)
    public void BuildMesh()
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        var uvs = new List<Vector2>();

        var colVerts = new List<Vector3>();
        var colTris = new List<int>();
        var litRanges = new List<Vector3Int>();

        for (int x = 0; x < SizeX; x++)
            for (int y = 0; y < SizeY; y++)
                for (int z = 0; z < SizeZ; z++)
                {
                    BlockType type = blocks[x, y, z];
                    BlockInfo info = BlockDatabase.Get(type);
                    if (!info.hasMesh) continue;

                    var pos = new Vector3(x, y, z);

                    // ---------- Rendu ----------
                    int vertStart = vertices.Count;
                    switch (info.shape)
                    {
                        case BlockShape.Cross:
                            AddCross(vertices, normals, uvs, triangles, pos, info, x, y, z);
                            break;

                        case BlockShape.Model:
                            AddModelQuads(vertices, normals, uvs, triangles, pos, info, type, x, y, z);
                            foreach (Element el in info.elements ?? NoElements)
                            {
                                for (int f = 0; f < 6; f++)
                                {
                                    if (el.tiles[f] < 0) continue;

                                    // Une face posée contre le bord du bloc est inutile si le voisin la cache
                                    if (IsFlush(el.min, el.max, f))
                                    {
                                        BlockType nb = GetNeighbor(x, y, z, f);
                                        if (BlockDatabase.Get(nb).opaque) continue;
                                        if (nb == type && info.cullSameType) continue;
                                    }

                                    AddBoxFace(vertices, triangles, normals, uvs, pos, el.min, el.max, f, el.tiles[f]);
                                }
                            }
                            break;

                        default: // Cube
                            for (int f = 0; f < 6; f++)
                            {
                                if (!BlockDatabase.ShouldDrawFace(type, GetNeighbor(x, y, z, f))) continue;
                                AddBoxFace(vertices, triangles, normals, uvs, pos,
                                           Vector3.zero, Vector3.one, f, BlockDatabase.GetTile(type, f));
                                AddBoxFace(colVerts, colTris, null, null, pos, Vector3.zero, Vector3.one, f, 0);
                            }
                            break;
                    }

                    if (info.lightMode != LightMode.Pixel && vertices.Count > vertStart)
                        litRanges.Add(new Vector3Int(vertStart, vertices.Count, (int)info.lightMode));
                    // ---------- Collision (mesh utilisé pour les raycasts) ----------
                    //if (info.collisionBoxes == null) continue;

                    if (info.shape != BlockShape.Cube)
                        for (int f = 0; f < 6; f++)
                            AddBoxFace(colVerts, colTris, null, null, pos, Vector3.zero, Vector3.one, f, 0);
                    //else if (info.shape != BlockShape.Cube && info.selectionBoxes == null)
                    //{
                    //        foreach (Element el in info.elements ?? NoElements)
                    //        {
                    //            for (int f = 0; f < 6; f++)
                    //            {
                    //                if (el.tiles[f] < 0) continue;

                    //                // Une face posée contre le bord du bloc est inutile si le voisin la cache
                    //                if (IsFlush(el.min, el.max, f))
                    //                {
                    //                    BlockType nb = GetNeighbor(x, y, z, f);
                    //                    if (BlockDatabase.Get(nb).opaque) continue;
                    //                    if (nb == type && info.cullSameType) continue;
                    //                }

                    //                AddBoxFace(colVerts, colTris, null, null, pos, el.min, el.max, f, 0);
                    //            }
                    //        }
                    //    }
                    //}
                }
        var colors = new Color32[vertices.Count];
        foreach (Vector3Int r in litRanges)
            for (int i = r.x; i < r.y; i++)
                colors[i] = new Color32(255, 255, 255, (byte)r.z);

        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.RecalculateBounds();
        meshFilter.sharedMesh = mesh;
        IsMeshed = true;

        colliderMesh.Clear();
        colliderMesh.SetVertices(colVerts);
        colliderMesh.SetTriangles(colTris, 0);

        meshCollider.sharedMesh = null; // force la mise à jour du collider
        if (colTris.Count > 0)
            meshCollider.sharedMesh = colliderMesh;
    }

    // La face f de la boîte touche-t-elle le bord du bloc ?
    static bool IsFlush(Vector3 min, Vector3 max, int f)
    {
        const float e = 1e-4f;
        switch (f)
        {
            case 0: return max.y >= 1f - e;
            case 1: return min.y <= e;
            case 2: return max.z >= 1f - e;
            case 3: return min.z <= e;
            case 4: return max.x >= 1f - e;
            default: return min.x <= e;
        }
    }

    // Ajoute une face d'une boîte (min..max en coordonnées de bloc).
    // normals/uvs peuvent être null (mesh de collision).
    static void AddBoxFace(List<Vector3> verts, List<int> tris, List<Vector3> normals, List<Vector2> uvs,
                           Vector3 blockPos, Vector3 min, Vector3 max, int face, int tile)
    {
        int start = verts.Count;
        Vector3 size = max - min;

        for (int i = 0; i < 4; i++)
        {
            Vector3 local = min + Vector3.Scale(FaceVerts[face][i], size);
            verts.Add(blockPos + local);

            if (normals != null)
            {
                normals.Add(Dirs[face]);
                uvs.Add(BlockDatabase.FaceUV(tile, face, local));
            }
        }

        tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
        tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
    }

    // Ajoute les quads d'un modèle importé (Blockbench)
    void AddModelQuads(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
                       Vector3 pos, BlockInfo info, BlockType type, int x, int y, int z)
    {
        if (info.quads == null) return;

        foreach (ModelQuad q in info.quads)
        {
            // Face cachée par un voisin opaque
            if (q.cullFace >= 0)
            {
                BlockType nb = GetNeighbor(x, y, z, q.cullFace);
                if (BlockDatabase.Get(nb).opaque) continue;
                if (nb == type && info.cullSameType) continue;
            }

            int start = verts.Count;

            verts.Add(pos + q.p0); verts.Add(pos + q.p1); verts.Add(pos + q.p2); verts.Add(pos + q.p3);
            uvs.Add(q.uv0); uvs.Add(q.uv1); uvs.Add(q.uv2); uvs.Add(q.uv3);
            for (int i = 0; i < 4; i++) normals.Add(q.normal);

            tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
            tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
        }
    }

    Vector3 PlantOffset(BlockInfo info, int x, int z)
    {
        if (!info.randomOffset) return Vector3.zero;

        int wx = Coord.x * SizeX + x;
        int wz = Coord.y * SizeZ + z;
        return new Vector3((Hash01(wx, wz, 1) - 0.5f) * 0.36f, 0f, (Hash01(wx, wz, 2) - 0.5f) * 0.36f);
    }

    // Deux plans en diagonale, chacun visible des deux côtés
    void AddCross(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
                      Vector3 pos, BlockInfo info, int x, int y, int z)
    {
        // Comme Minecraft : les plans font 0,9 de long, centrés dans le bloc
        const float h = 0.318f; // 0.45 * cos(45°)
        float a = 0.5f - h;
        float b = 0.5f + h;

        Vector3 p = pos + PlantOffset(info, x, z);

        int tile = info.tileSide;
        AddPlane(verts, normals, uvs, tris, p + new Vector3(a, 0, a), p + new Vector3(b, 0, b), tile);
        AddPlane(verts, normals, uvs, tris, p + new Vector3(b, 0, a), p + new Vector3(a, 0, b), tile);
    }

    // Un plan vertical de `start` à `end` (au sol), avec face avant et face arrière
    static void AddPlane(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
                         Vector3 start, Vector3 end, int tile)
    {
        // bas-début, haut-début, haut-fin, bas-fin
        Vector3 p0 = start;
        Vector3 p1 = start + Vector3.up;
        Vector3 p2 = end + Vector3.up;
        Vector3 p3 = end;

        Vector2 uv0 = BlockDatabase.TileUV(tile, 0f, 0f);
        Vector2 uv1 = BlockDatabase.TileUV(tile, 0f, 1f);
        Vector2 uv2 = BlockDatabase.TileUV(tile, 1f, 1f);
        Vector2 uv3 = BlockDatabase.TileUV(tile, 1f, 0f);

        AddQuad(verts, normals, uvs, tris, p0, p1, p2, p3, uv0, uv1, uv2, uv3); // avant
        AddQuad(verts, normals, uvs, tris, p0, p3, p2, p1, uv0, uv3, uv2, uv1); // arrière (ordre inversé)
    }

    // Les normales pointent vers le HAUT : les plantes sont éclairées comme un dessus de bloc,
    // et le shader garde une normale alignée sur un axe.
    static void AddQuad(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
                        Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                        Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
    {
        int start = verts.Count;

        verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
        uvs.Add(uvA); uvs.Add(uvB); uvs.Add(uvC); uvs.Add(uvD);
        for (int i = 0; i < 4; i++) normals.Add(Vector3.up);

        tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
        tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
    }
}