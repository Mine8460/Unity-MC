using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

// Les tableaux de sortie d'une construction de mesh. Réutilisés (pool) pour ne pas allouer à chaque chunk :
// ce sont les allocations répétées qui déclenchent le ramasse-miettes, donc les saccades.
public sealed class MeshBuffers
{
    public readonly List<Vector3> vertices = new List<Vector3>(8192);
    public readonly List<Vector3> normals = new List<Vector3>(8192);
    public readonly List<Vector2> uvs = new List<Vector2>(8192);
    public readonly List<int> triangles = new List<int>(16384);

    // Triangles de l'eau : mêmes sommets que les blocs, mais dessinés avec le matériau de l'eau (sous-mesh 1)
    public readonly List<int> waterTriangles = new List<int>(4096);
    public readonly List<int> translucentTriangles = new List<int>(1024);   // faces à transparence partielle (verre...)
    public readonly List<Color32> colors = new List<Color32>(8192);

    // Mesh de raycast (casser / poser des blocs) : tout ce qui est visable, même sans collision
    public readonly List<Vector3> colVerts = new List<Vector3>(4096);
    public readonly List<int> colTris = new List<int>(8192);

    // (début, fin, mode) des sommets à éclairage spécial (plantes, torche) : voir LightMode
    public readonly List<Vector3Int> litRanges = new List<Vector3Int>(64);

    // Sens du courant (x, z) de chaque sommet, rangé dans le canal UV1 : le shader de l'eau fait défiler sa
    // texture dans ce sens. Zéro partout, sauf sur le dessus de l'eau qui coule.
    public readonly List<Vector2> flow = new List<Vector2>(16384);
    public readonly List<(int start, Vector2 dir)> waterFlow = new List<(int start, Vector2 dir)>(256);

    // Smooth lighting : sommets de l'eau (éclairage plat, une valeur par face), sommets à éclairage plat,
    // et faces dont il faut retourner la diagonale (voir ChunkMesher.ApplyVertexLight)
    public readonly List<Vector2Int> waterRanges = new List<Vector2Int>(64);
    public readonly List<bool> flat = new List<bool>(16384);
    public readonly HashSet<int> flipQuads = new HashSet<int>();

    // Redstone : (début, fin, puissance 0 à 15) des sommets teintés selon la puissance (fil, torches du répéteur)
    public readonly List<Vector3Int> wirePower = new List<Vector3Int>(64);

    public bool failed;

    public void Clear()
    {
        vertices.Clear();
        normals.Clear();
        uvs.Clear();
        triangles.Clear();
        waterTriangles.Clear();
        translucentTriangles.Clear();
        colors.Clear();
        colVerts.Clear();
        colTris.Clear();
        litRanges.Clear();
        flow.Clear();
        waterFlow.Clear();
        waterRanges.Clear();
        flat.Clear();
        flipQuads.Clear();
        wirePower.Clear();
        failed = false;
    }

    static readonly ConcurrentBag<MeshBuffers> pool = new ConcurrentBag<MeshBuffers>();

    public static MeshBuffers Rent()
    {
        return pool.TryTake(out MeshBuffers b) ? b : new MeshBuffers();
    }

    public static void Return(MeshBuffers b)
    {
        b.Clear();
        pool.Add(b);
    }
}

// Ce dont le mesh d'un chunk a besoin : ses données, et celles de ses 4 voisins (pour masquer les faces
// contre les bords et lire la lumière de l'autre côté). Un voisin null est traité comme de l'air.
public struct ChunkSnapshot
{
    public ChunkData center;
    public ChunkData px;   // chunk voisin en +X
    public ChunkData nx;   // chunk voisin en -X
    public ChunkData pz;   // chunk voisin en +Z
    public ChunkData nz;   // chunk voisin en -Z

    // Voisins en diagonale (null s'ils ne sont pas chargés) : seulement pour la hauteur des coins de l'eau
    public ChunkData pxpz, pxnz, nxpz, nxnz;
}

// Construction du mesh d'un chunk. Thread-safe : ne lit que le ChunkSnapshot et n'écrit que dans MeshBuffers.
// Aucun appel à un objet Unity (GameObject, Mesh...) : seules des structures simples (Vector3, Color32).
public sealed class ChunkMesher
{
    readonly ChunkSnapshot s;
    readonly MeshBuffers buf;
    readonly int originX;
    readonly int originZ;

    // Les 6 voisins du bloc en cours (haut, bas, +Z, -Z, +X, -X), lus UNE seule fois puis réutilisés
    // pour le rendu et pour le mesh de raycast
    readonly BlockType[] nb = new BlockType[6];

    // Lecture des blocs (chunk et voisins) pour les règles de la redstone ; une case inconnue compte comme de l'air
    sealed class RedstoneView : IBlockView
    {
        readonly ChunkMesher m;
        public RedstoneView(ChunkMesher mesher) { m = mesher; }

        public BlockType TypeAt(int x, int y, int z) => m.TryCell(x, y, z, out BlockType t, out _) ? t : BlockType.Air;
        public byte StateAt(int x, int y, int z) => m.TryCell(x, y, z, out _, out byte st) ? st : (byte)0;
    }

    readonly RedstoneView view;
    readonly int[] wireConn = new int[4];

    ChunkMesher(ChunkSnapshot snapshot, MeshBuffers buffers)
    {
        view = new RedstoneView(this);
        s = snapshot;
        buf = buffers;
        originX = snapshot.center.coord.x * Chunk.SizeX;
        originZ = snapshot.center.coord.y * Chunk.SizeZ;
    }

    // Construit le mesh complet du chunk dans `buffers`
    public static void Build(ChunkSnapshot snapshot, MeshBuffers buffers)
    {
        buffers.Clear();
        new ChunkMesher(snapshot, buffers).Run();
    }

    // ------------------------------------------------------------------
    // Géométrie commune (aussi utilisée par Chunk.BuildBlockMesh : blocs qui tombent, objets, icônes)
    // ------------------------------------------------------------------

    static readonly Element[] NoElements = new Element[0];

    // Direction de chaque face : haut, bas, +Z, -Z, +X, -X
    static readonly int[] DirX = { 0, 0, 0, 0, 1, -1 };
    static readonly int[] DirY = { 1, -1, 0, 0, 0, 0 };
    static readonly int[] DirZ = { 0, 0, 1, -1, 0, 0 };

    public static readonly Vector3[] FaceNormals =
    {
        new Vector3(0, 1, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1),
        new Vector3(0, 0, -1), new Vector3(1, 0, 0), new Vector3(-1, 0, 0)
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

    // La face f de la boîte touche-t-elle le bord du bloc ?
    public static bool IsFlush(Vector3 min, Vector3 max, int f)
    {
        const float e = 1e-4f;
        switch (f)
        {
            case 0:  return max.y >= 1f - e;
            case 1:  return min.y <= e;
            case 2:  return max.z >= 1f - e;
            case 3:  return min.z <= e;
            case 4:  return max.x >= 1f - e;
            default: return min.x <= e;
        }
    }

    // Ajoute une face d'une boîte (min..max en coordonnées de bloc).
    // normals/uvs peuvent être null (mesh de collision).
    public static void AddBoxFace(List<Vector3> verts, List<int> tris, List<Vector3> normals, List<Vector2> uvs,
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
                normals.Add(FaceNormals[face]);
                uvs.Add(BlockDatabase.FaceUV(tile, face, local));
            }
        }

        tris.Add(start);     tris.Add(start + 1); tris.Add(start + 2);
        tris.Add(start);     tris.Add(start + 2); tris.Add(start + 3);
    }

    // Un plan vertical de `start` à `end` (au sol), avec face avant et face arrière
    public static void AddPlane(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
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

        tris.Add(start);     tris.Add(start + 1); tris.Add(start + 2);
        tris.Add(start);     tris.Add(start + 2); tris.Add(start + 3);
    }

    // Décalage horizontal pseudo-aléatoire d'une plante à une position MONDE (identique pour le rendu,
    // le raycast et le contour du bloc visé)
    public static Vector3 PlantOffsetAt(int worldX, int worldZ)
    {
        // ±0.18 : avec des plans de ±0.318, la plante ne sort jamais de son bloc (le shader en a besoin)
        return new Vector3(
            (TerrainGenerator.Hash01(worldX, worldZ, 1) - 0.5f) * 0.36f, 0f,
            (TerrainGenerator.Hash01(worldX, worldZ, 2) - 0.5f) * 0.36f);
    }

    // ------------------------------------------------------------------
    // Lecture des blocs et de la lumière, y compris chez les 4 voisins
    // ------------------------------------------------------------------

    // Renvoie le ChunkData qui contient la case (x, z) (coordonnées locales, éventuellement hors du chunk
    // d'une seule case) et ramène x, z dans [0, 16). null = voisin absent ou diagonale (jamais demandée).
    ChunkData Locate(ref int x, ref int z)
    {
        bool xIn = (uint)x < (uint)Chunk.SizeX;
        bool zIn = (uint)z < (uint)Chunk.SizeZ;

        if (xIn && zIn) return s.center;
        if (zIn)
        {
            if (x < 0) { x += Chunk.SizeX; return s.nx; }
            x -= Chunk.SizeX;
            return s.px;
        }
        if (xIn)
        {
            if (z < 0) { z += Chunk.SizeZ; return s.nz; }
            z -= Chunk.SizeZ;
            return s.pz;
        }

        // Diagonale
        ChunkData d;
        if (x < 0) { x += Chunk.SizeX; d = z < 0 ? s.nxnz : s.nxpz; }
        else       { x -= Chunk.SizeX; d = z < 0 ? s.pxnz : s.pxpz; }
        if (z < 0) z += Chunk.SizeZ; else z -= Chunk.SizeZ;
        return d;
    }

    // Comme BlockAt, mais dit aussi si la case est connue (false = chunk voisin pas chargé)
    bool TryCell(int x, int y, int z, out BlockType type, out byte blockState)
    {
        type = BlockType.Air;
        blockState = 0;
        if (y >= Chunk.SizeY) return true;   // au-dessus du monde : de l'air
        if (y < 0) return false;

        ChunkData d = Locate(ref x, ref z);
        if (d == null) return false;

        int i = ChunkData.Index(x, y, z);
        type = (BlockType)d.blocks[i];
        blockState = d.state[i];
        return true;
    }

    BlockType BlockAt(int x, int y, int z)
    {
        if ((uint)y >= (uint)Chunk.SizeY) return BlockType.Air;

        ChunkData d = Locate(ref x, ref z);
        if (d == null) return BlockType.Air;
        return (BlockType)d.blocks[ChunkData.Index(x, y, z)];
    }

    // Lumière packée (bits 0-3 torches, 4-7 ciel) à une case, voisins compris
    int LightAt(int x, int y, int z)
    {
        if (y >= Chunk.SizeY) return 0xF0;  // au-dessus du monde : ciel ouvert
        if (y < 0) return 0;

        ChunkData d = Locate(ref x, ref z);
        if (d == null) return 0;
        return d.light[ChunkData.Index(x, y, z)];
    }

    BlockType Neighbor(int x, int y, int z, int face)
    {
        return BlockAt(x + DirX[face], y + DirY[face], z + DirZ[face]);
    }

    // Lit les 6 voisins d'une case. Au milieu du chunk (cas de loin le plus fréquent), ce sont de simples
    // lectures de tableau à des distances fixes, sans chercher dans quel chunk on se trouve.
    void FillNeighbors(int x, int y, int z)
    {
        if (x > 0 && x < Chunk.SizeX - 1 && z > 0 && z < Chunk.SizeZ - 1 && y > 0 && y < Chunk.SizeY - 1)
        {
            byte[] b = s.center.blocks;
            int i = ChunkData.Index(x, y, z);
            nb[0] = (BlockType)b[i + 1];                          // haut
            nb[1] = (BlockType)b[i - 1];                          // bas
            nb[2] = (BlockType)b[i + Chunk.SizeY];                // +Z
            nb[3] = (BlockType)b[i - Chunk.SizeY];                // -Z
            nb[4] = (BlockType)b[i + Chunk.SizeZ * Chunk.SizeY];  // +X
            nb[5] = (BlockType)b[i - Chunk.SizeZ * Chunk.SizeY];  // -X
            return;
        }

        for (int f = 0; f < 6; f++) nb[f] = Neighbor(x, y, z, f);
    }

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    void Run()
    {
        ChunkData c = s.center;
        int top = Math.Min(c.highest, Chunk.SizeY - 1);

        // On ne parcourt que la partie occupée du chunk : tout ce qui est au-dessus est de l'air
        for (int x = 0; x < Chunk.SizeX; x++)
        for (int z = 0; z < Chunk.SizeZ; z++)
        {
            int col = ChunkData.Index(x, 0, z);
            for (int y = 0; y <= top; y++)
            {
                BlockType type = (BlockType)c.blocks[col + y];
                if (!BlockDatabase.HasMesh(type)) continue;

                AddBlock(type, x, y, z);
            }
        }

        // Couleur des sommets : alpha = mode d'éclairage (0 = pixels d'ombre, voir LightMode)
        int count = buf.vertices.Count;
        for (int i = 0; i < count; i++) buf.colors.Add(new Color32(0, 0, 0, 0));

        foreach (Vector3Int r in buf.litRanges)
            for (int i = r.x; i < r.y; i++)
                buf.colors[i] = new Color32(255, 255, 255, (byte)r.z);

        // Sens du courant (UV1) : zéro, sauf sur le dessus de l'eau
        for (int i = 0; i < count; i++) buf.flow.Add(Vector2.zero);
        foreach (var wf in buf.waterFlow)
            for (int k = 0; k < 4; k++) buf.flow[wf.start + k] = wf.dir;

        // Lumière des torches (R) et du ciel (G) de chaque face
        ApplyVertexLight();

        // Faces dont la tuile a de la transparence partielle : sous-mesh à part (mélange alpha)
        SplitTranslucent();

        // Redstone : la puissance (0 à 15) dans le canal bleu, que le shader transforme en teinte
        foreach (Vector3Int w in buf.wirePower)
            for (int i = w.x; i < w.y; i++)
            {
                Color32 prev = buf.colors[i];
                buf.colors[i] = new Color32(prev.r, prev.g, (byte)(w.z * 17), prev.a);
            }
    }

    // Orientation du bloc en cours (état du bloc, voir BlockOrientation) ; curState = 0 : tel que dessiné
    Orientation curOrient;
    int curState;

    // Face du MONDE où se retrouve la face « dessinée » f du bloc en cours
    int DstFace(int f) => curState == 0 ? f : BlockOrientation.RotateFace(curOrient, curState, f);

    // Tourne les sommets (et normales) ajoutés depuis `start` selon l'orientation du bloc
    void RotateAdded(List<Vector3> verts, List<Vector3> norms, int start, Vector3 pos)
    {
        for (int i = start; i < verts.Count; i++)
            verts[i] = pos + BlockOrientation.RotateLocal(curOrient, curState, verts[i] - pos);

        if (norms == null) return;
        for (int i = start; i < norms.Count; i++)
            norms[i] = BlockOrientation.RotateDir(curOrient, curState, norms[i]);
    }

    void AddBlock(BlockType type, int x, int y, int z)
    {
        ref readonly BlockInfo info = ref BlockDatabase.GetRef(type);
        var pos = new Vector3(x, y, z);

        // L'eau a son propre rendu, et n'entre pas dans le mesh de raycast : on vise à travers
        if (info.shape == BlockShape.Liquid)
        {
            AddLiquid(type, x, y, z, pos);
            return;
        }

        // Orientation : lue dans l'état du bloc
        curOrient = info.orientation;
        curState = 0;
        if (curOrient != Orientation.None)
        {
            TryCell(x, y, z, out _, out byte orientState);
            curState = BlockOrientation.Clamp(curOrient, orientState);
        }

        // ---------- Rendu ----------
        int vertStart = buf.vertices.Count;
        switch (info.shape)
        {
            case BlockShape.Cross:
                AddCross(in info, pos, x, z);
                break;

            case BlockShape.Model:
                AddModel(in info, type, pos, x, y, z);
                break;

            case BlockShape.Wire:
                AddWire(in info, pos, x, y, z);
                break;

            case BlockShape.Repeater:
                AddRepeater(in info, pos, x, y, z);
                break;

            default: // Cube
                FillNeighbors(x, y, z);
                for (int f = 0; f < 6; f++)
                {
                    if (!BlockDatabase.ShouldDrawFace(type, nb[DstFace(f)])) continue;
                    AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos,
                               Vector3.zero, Vector3.one, f, BlockDatabase.GetTile(type, f));
                }
                break;
        }

        // Bloc tourné : on tourne ce qu'on vient de dessiner (les textures suivent la géométrie)
        if (curState != 0 && buf.vertices.Count > vertStart)
            RotateAdded(buf.vertices, buf.normals, vertStart, pos);

        if (info.lightMode != LightMode.Pixel && buf.vertices.Count > vertStart)
            buf.litRanges.Add(new Vector3Int(vertStart, buf.vertices.Count, (int)info.lightMode));

        // ---------- Mesh de raycast (casser / poser des blocs) ----------
        // Tous les blocs visibles doivent pouvoir être visés, même ceux sans collision
        // (herbe, feuilles, torche). La collision du joueur n'utilise PAS ce mesh :
        // elle passe par BlockDatabase (collisionBoxes), donc elle reste inchangée.
        if (info.shape == BlockShape.Cube)
        {
            for (int f = 0; f < 6; f++)
            {
                if (BlockDatabase.IsFullCube(nb[f])) continue;
                AddBoxFace(buf.colVerts, buf.colTris, null, null, pos, Vector3.zero, Vector3.one, f, 0);
            }
        }
        else if (info.selectionBoxes != null)
        {
            int colStart = buf.colVerts.Count;
            Vector3 off = Vector3.zero;
            if (info.shape == BlockShape.Cross && info.randomOffset)
                off = PlantOffsetAt(originX + x, originZ + z);

            foreach (Box b in info.selectionBoxes)
            {
                // Reste dans le bloc pour que "point touché - normale" retombe sur la bonne case
                Vector3 bmin = Vector3.Max(b.min + off, Vector3.zero);
                Vector3 bmax = Vector3.Min(b.max + off, Vector3.one);
                for (int f = 0; f < 6; f++)
                    AddBoxFace(buf.colVerts, buf.colTris, null, null, pos, bmin, bmax, f, 0);
            }

            if (curState != 0) RotateAdded(buf.colVerts, null, colStart, pos);
        }
    }

    // ------------------------------------------------------------------
    // Redstone
    // ------------------------------------------------------------------

    const float WireHeight = 0.02f;   // au-dessus du bloc : évite de clignoter avec sa face

    // Poussière : un point, ou un trait qui suit ses raccords (lignes, coins, croisements). Quand un raccord monte sur
    // le bloc voisin, un trait grimpe aussi le long de sa face. La couleur dépend de la puissance (lue dans l'état).
    void AddWire(in BlockInfo info, Vector3 pos, int x, int y, int z)
    {
        TryCell(x, y, z, out _, out byte power);
        Redstone.WireShape(view, x, y, z, wireConn);

        int start = buf.vertices.Count;
        int tile = info.tileSide;
        const float a0 = 6f / 16f, a1 = 10f / 16f;

        bool connected = wireConn[0] > 0 || wireConn[1] > 0 || wireConn[2] > 0 || wireConn[3] > 0;
        if (!connected) FlatWirePatch(pos, 5f / 16f, 5f / 16f, 11f / 16f, 11f / 16f, tile);   // un point
        else FlatWirePatch(pos, a0, a0, a1, a1, tile);                                       // le centre

        // Branches : nord (+Z), est (+X), sud (-Z), ouest (-X)
        if (wireConn[0] > 0) FlatWirePatch(pos, a0, a1, a1, 1f, tile);
        if (wireConn[1] > 0) FlatWirePatch(pos, a1, a0, 1f, a1, tile);
        if (wireConn[2] > 0) FlatWirePatch(pos, a0, 0f, a1, a0, tile);
        if (wireConn[3] > 0) FlatWirePatch(pos, 0f, a0, a0, a1, tile);

        // Branches qui montent : un trait vertical contre la face du bloc voisin
        const float e = 0.02f;
        if (wireConn[0] == 2) AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos, new Vector3(a0, 0f, 1f - e), new Vector3(a1, 1f, 1f - e * 0.5f), 3, tile);
        if (wireConn[1] == 2) AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos, new Vector3(1f - e, 0f, a0), new Vector3(1f - e * 0.5f, 1f, a1), 5, tile);
        if (wireConn[2] == 2) AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos, new Vector3(a0, 0f, e * 0.5f), new Vector3(a1, 1f, e), 2, tile);
        if (wireConn[3] == 2) AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos, new Vector3(e * 0.5f, 0f, a0), new Vector3(e, 1f, a1), 4, tile);

        MarkWire(start, power);
    }

    // Un rectangle à plat sur le sol, légèrement au-dessus
    void FlatWirePatch(Vector3 pos, float x0, float z0, float x1, float z1, int tile)
    {
        AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos,
                   new Vector3(x0, 0f, z0), new Vector3(x1, WireHeight, z1), 0, tile);
    }

    // Les sommets ajoutés depuis `start` sont teintés selon la puissance (mode d'éclairage « Wire » du shader)
    void MarkWire(int start, int power)
    {
        int end = buf.vertices.Count;
        if (end <= start) return;

        buf.litRanges.Add(new Vector3Int(start, end, (int)LightMode.Wire));
        buf.wirePower.Add(new Vector3Int(start, end, power));
    }

    // Répéteur : une dalle de 2/16 dont le dessus (une flèche) est tourné vers sa sortie, et deux torches.
    // La torche du fond est fixe ; l'autre recule quand le délai augmente.
    void AddRepeater(in BlockInfo info, Vector3 pos, int x, int y, int z)
    {
        TryCell(x, y, z, out _, out byte state);
        int facing = Redstone.RepeaterFacing(state);
        int delay = Redstone.RepeaterDelay(state);
        bool powered = Redstone.RepeaterPowered(state);

        const float h = 2f / 16f;
        var min = Vector3.zero;
        var max = new Vector3(1f, h, 1f);

        for (int f = 2; f < 6; f++)
            AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos, min, max, f, info.tileSide);

        // Dessus : la texture tournée pour que sa flèche (vers le haut de l'image) regarde la sortie
        int top = buf.vertices.Count;
        buf.vertices.Add(pos + new Vector3(0f, h, 0f));
        buf.vertices.Add(pos + new Vector3(0f, h, 1f));
        buf.vertices.Add(pos + new Vector3(1f, h, 1f));
        buf.vertices.Add(pos + new Vector3(1f, h, 0f));
        for (int k = 0; k < 4; k++)
        {
            Vector3 v = buf.vertices[top + k] - pos;
            Vector2 uv = RotatedTopUV(v.x, v.z, facing);
            buf.normals.Add(Vector3.up);
            buf.uvs.Add(BlockDatabase.TileUV(info.tileTop, uv.x, uv.y));
        }
        buf.triangles.Add(top); buf.triangles.Add(top + 1); buf.triangles.Add(top + 2);
        buf.triangles.Add(top); buf.triangles.Add(top + 2); buf.triangles.Add(top + 3);

        // Les deux torches (teintées : rouge vif quand le répéteur est allumé, sombre sinon)
        int start = buf.vertices.Count;
        Vector3Int fwd = Redstone.Horizontal[facing];
        float outputPos = 0.8125f;                    // près de la sortie
        float delayPos = 0.6875f - 0.125f * delay;    // recule avec le délai
        AddRepeaterTorch(pos, fwd, outputPos, h, info.tileBottom);
        AddRepeaterTorch(pos, fwd, delayPos, h, info.tileBottom);
        MarkWire(start, powered ? Redstone.MaxPower : 0);
    }

    // Une petite torche de 2 x 5 x 2 pixels, à la distance s (0 = fond, 1 = sortie) le long du sens du répéteur
    void AddRepeaterTorch(Vector3 pos, Vector3Int fwd, float s, float baseHeight, int tile)
    {
        float cx = 0.5f + fwd.x * (s - 0.5f);
        float cz = 0.5f + fwd.z * (s - 0.5f);
        const float r = 1f / 16f;
        var min = new Vector3(cx - r, baseHeight, cz - r);
        var max = new Vector3(cx + r, baseHeight + 5f / 16f, cz + r);

        for (int f = 0; f < 6; f++)
        {
            if (f == 1) continue; // le dessous est caché
            AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos, min, max, f, tile);
        }
    }

    // UV du dessus du répéteur : la flèche de la texture (vers le haut de l'image) suit le sens de sortie
    static Vector2 RotatedTopUV(float x, float z, int facing)
    {
        switch (facing)
        {
            case 0:  return new Vector2(x, z);               // sortie au nord (+Z)
            case 1:  return new Vector2(1f - z, x);          // est (+X)
            case 2:  return new Vector2(1f - x, 1f - z);     // sud (-Z)
            default: return new Vector2(z, 1f - x);          // ouest (-X)
        }
    }

    // Hauteur de la surface d'une source dans son bloc (comme Minecraft : un peu sous le bord)
    const float LiquidSurface = 14f / 16f;

    // Hauteur de l'eau dans sa case (0 à 1) selon son niveau : une source est presque pleine,
    // une eau qui coule baisse à chaque bloc qui l'éloigne de sa source
    float LiquidHeight(BlockType water, int x, int y, int z, byte blockState)
    {
        if (BlockAt(x, y + 1, z) == water) return 1f;
        if (blockState == WaterState.Source || blockState == WaterState.Falling) return LiquidSurface;
        return LiquidSurface * (8 - blockState) / 8f;
    }

    // Hauteur d'un COIN de la surface : moyenne des 4 cases qui le partagent (comme Minecraft). Deux cases
    // voisines calculent la même valeur pour leur coin commun : la surface est continue, en pente douce.
    // (cx, cz) : coordonnées locales du coin (0 à 16).
    float CornerHeight(BlockType water, int cx, int y, int cz)
    {
        float sum = 0f, weight = 0f;

        for (int dx = -1; dx <= 0; dx++)
        for (int dz = -1; dz <= 0; dz++)
        {
            int x = cx + dx, z = cz + dz;
            if (!TryCell(x, y, z, out BlockType type, out byte blockState)) continue; // chunk pas chargé : ignoré

            if (type == water)
            {
                if (BlockAt(x, y + 1, z) == water) return 1f; // de l'eau au-dessus : le coin monte jusqu'en haut
                float w = blockState == WaterState.Source ? 10f : 1f; // une source tient sa hauteur
                sum += LiquidHeight(water, x, y, z, blockState) * w;
                weight += w;
            }
            else
            {
                ref readonly BlockInfo info = ref BlockDatabase.GetRef(type);
                if (!info.opaque && !info.collidable) weight += 1f; // l'air tire le bord vers le bas ; un bloc solide est ignoré
            }
        }

        return weight > 0f ? sum / weight : LiquidSurface;
    }

    // Eau : une face seulement contre ce qui n'est ni de l'eau ni opaque (l'air, la vitre, les feuilles...).
    // Le dessus est incliné d'après la hauteur de ses 4 coins ; les côtés montent jusqu'à ces coins.
    void AddLiquid(BlockType type, int x, int y, int z, Vector3 pos)
    {
        int firstVertex = buf.vertices.Count;
        AddLiquidFaces(type, x, y, z, pos);
        buf.waterRanges.Add(new Vector2Int(firstVertex, buf.vertices.Count)); // l'eau garde un éclairage plat
    }

    void AddLiquidFaces(BlockType type, int x, int y, int z, Vector3 pos)
    {
        FillNeighbors(x, y, z);

        // Hauteur des 4 coins du dessus
        float h00, h01, h11, h10;
        if (nb[0] == type)
        {
            h00 = h01 = h11 = h10 = 1f; // de l'eau au-dessus : bloc plein
        }
        else
        {
            h00 = CornerHeight(type, x, y, z);
            h01 = CornerHeight(type, x, y, z + 1);
            h11 = CornerHeight(type, x + 1, y, z + 1);
            h10 = CornerHeight(type, x + 1, y, z);
        }

        bool fullHeight = h00 >= 1f && h01 >= 1f && h11 >= 1f && h10 >= 1f;

        // Sens du courant : vers le bas de la pente de la surface (zéro sur un lac, où tout est à la même hauteur)
        var downhill = new Vector2((h00 + h01) - (h10 + h11), (h00 + h10) - (h01 + h11)) * 0.5f;

        for (int f = 0; f < 6; f++)
        {
            BlockType n = nb[f];
            if (n == type) continue;

            // Contre un bloc opaque, la face est cachée. Sauf le dessus : la surface est plus basse que le bloc
            // posé sur l'eau, et on la voit par l'interstice entre les deux.
            if (BlockDatabase.IsOpaque(n) && (f != 0 || fullHeight)) continue;

            int tile = BlockDatabase.GetTile(type, f);
            int start = buf.vertices.Count;
            if (f == 0) buf.waterFlow.Add((start, downhill));

            for (int i = 0; i < 4; i++)
            {
                Vector3 v = FaceVerts[f][i];
                float top = v.x < 0.5f ? (v.z < 0.5f ? h00 : h01) : (v.z < 0.5f ? h10 : h11);
                var local = new Vector3(v.x, v.y > 0.5f ? top : 0f, v.z);

                buf.vertices.Add(pos + local);
                buf.normals.Add(FaceNormals[f]);
                buf.uvs.Add(BlockDatabase.FaceUV(tile, f, local));
            }

            buf.waterTriangles.Add(start);     buf.waterTriangles.Add(start + 1); buf.waterTriangles.Add(start + 2);
            buf.waterTriangles.Add(start);     buf.waterTriangles.Add(start + 2); buf.waterTriangles.Add(start + 3);
        }
    }

    void AddModel(in BlockInfo info, BlockType type, Vector3 pos, int x, int y, int z)
    {
        AddModelQuads(in info, type, pos, x, y, z);

        Element[] elements = info.elements ?? NoElements;
        for (int e = 0; e < elements.Length; e++)
        {
            Element el = elements[e];

            for (int f = 0; f < 6; f++)
            {
                if (el.tiles[f] < 0) continue;

                // Une face posée contre le bord du bloc est inutile si le voisin la cache
                if (IsFlush(el.min, el.max, f))
                {
                    BlockType nb = Neighbor(x, y, z, DstFace(f));
                    if (BlockDatabase.IsOpaque(nb)) continue;

                    // Même bloc voisin : les deux faces ne se touchent que si ce bloc a une face contre le
                    // bord de la case des DEUX côtés. Une dalle posée sur une autre laisse une demi-case de
                    // vide entre les deux : la face doit rester visible.
                    if (nb == type && info.cullSameType &&
                        (info.flushMask & (1 << f)) != 0 && (info.flushMask & (1 << (f ^ 1))) != 0) continue;
                }

                AddBoxFace(buf.vertices, buf.triangles, buf.normals, buf.uvs, pos, el.min, el.max, f, el.tiles[f]);
            }
        }
    }

    // Ajoute les quads d'un modèle importé (Blockbench)
    void AddModelQuads(in BlockInfo info, BlockType type, Vector3 pos, int x, int y, int z)
    {
        if (info.quads == null) return;

        for (int k = 0; k < info.quads.Length; k++)
        {
            ModelQuad q = info.quads[k];

            // Face cachée par un voisin opaque
            if (q.cullFace >= 0)
            {
                BlockType nb = Neighbor(x, y, z, DstFace(q.cullFace));
                if (BlockDatabase.IsOpaque(nb)) continue;
                if (nb == type && info.cullSameType) continue;
            }

            int start = buf.vertices.Count;

            buf.vertices.Add(pos + q.p0); buf.vertices.Add(pos + q.p1); buf.vertices.Add(pos + q.p2); buf.vertices.Add(pos + q.p3);
            buf.uvs.Add(q.uv0);           buf.uvs.Add(q.uv1);           buf.uvs.Add(q.uv2);           buf.uvs.Add(q.uv3);
            for (int i = 0; i < 4; i++) buf.normals.Add(q.normal);

            buf.triangles.Add(start);     buf.triangles.Add(start + 1); buf.triangles.Add(start + 2);
            buf.triangles.Add(start);     buf.triangles.Add(start + 2); buf.triangles.Add(start + 3);
        }
    }

    // Deux plans en diagonale, chacun visible des deux côtés
    void AddCross(in BlockInfo info, Vector3 pos, int x, int z)
    {
        // Comme Minecraft : les plans font 0,9 de long, centrés dans le bloc
        const float h = 0.318f; // 0.45 * cos(45°)
        float a = 0.5f - h;
        float b = 0.5f + h;

        Vector3 p = pos;
        if (info.randomOffset) p += PlantOffsetAt(originX + x, originZ + z);

        int tile = info.tileSide;
        AddPlane(buf.vertices, buf.normals, buf.uvs, buf.triangles, p + new Vector3(a, 0, a), p + new Vector3(b, 0, b), tile);
        AddPlane(buf.vertices, buf.normals, buf.uvs, buf.triangles, p + new Vector3(b, 0, a), p + new Vector3(a, 0, b), tile);
    }

    // Lumière des torches (R) et du ciel (G) de chaque face, dans les couleurs de sommet : 0 à 255 pour
    // les niveaux 0 à 15, B = 255. L'alpha (mode d'éclairage) est conservé.
    // Un échantillon par quad (4 sommets consécutifs), pris juste DEVANT la face.
    // Smooth lighting façon Minecraft (lumière lissée et occlusion ambiante aux coins des faces).
    // Réglé par World avant le démarrage des threads de maillage. Le changer demande de refaire les meshes.
    public static bool SmoothLighting = true;

    // Luminosité d'un coin selon son occlusion ambiante : 0 = coincé par deux blocs, 3 = libre (comme Minecraft)
    static readonly float[] AoBrightness = { 0.5f, 0.68f, 0.84f, 1f };

    // Couleur des sommets : R = lumière des torches, G = lumière du ciel (niveau x 17), B = occlusion ambiante,
    // A = mode d'éclairage (déjà rempli)
    void SplitTranslucent()
    {
        if (!BlockDatabase.HasTranslucentTiles) return;
        List<int> src = buf.triangles;
        int perRow = BlockDatabase.AtlasTilesPerRow;
        int keep = 0;
        for (int i = 0; i + 2 < src.Count; i += 3)
        {
            Vector2 a = buf.uvs[src[i]], b = buf.uvs[src[i + 1]], c = buf.uvs[src[i + 2]];
            float u = (a.x + b.x + c.x) / 3f, v = (a.y + b.y + c.y) / 3f;
            int col = Mathf.Clamp((int)(u * perRow), 0, perRow - 1);
            int row = Mathf.Clamp((int)((1f - v) * perRow), 0, perRow - 1);
            if (BlockDatabase.IsTranslucentTile(row * perRow + col))
            {
                buf.translucentTriangles.Add(src[i]); buf.translucentTriangles.Add(src[i + 1]); buf.translucentTriangles.Add(src[i + 2]);
            }
            else
            {
                src[keep++] = src[i]; src[keep++] = src[i + 1]; src[keep++] = src[i + 2];
            }
        }
        src.RemoveRange(keep, src.Count - keep);
    }

    void ApplyVertexLight()
    {
        List<Vector3> verts = buf.vertices;
        List<Vector3> normals = buf.normals;

        // Sommets qui gardent un éclairage plat : plantes et torche (modes spéciaux), eau
        int count = verts.Count;
        for (int i = 0; i < count; i++) buf.flat.Add(false);
        foreach (Vector3Int r in buf.litRanges)
            for (int i = r.x; i < r.y; i++) buf.flat[i] = true;
        foreach (Vector2Int r in buf.waterRanges)
            for (int i = r.x; i < r.y; i++) buf.flat[i] = true;

        for (int i = 0; i + 3 < count; i += 4)
        {
            Vector3 center = (verts[i] + verts[i + 1] + verts[i + 2] + verts[i + 3]) * 0.25f;
            Vector3 n = normals[i];

            if (!SmoothLighting || buf.flat[i] || !SmoothQuad(i, center, n))
                FlatQuad(i, center, n);
        }

        FlipDarkDiagonals();
    }

    // Éclairage plat : la lumière de la case devant la face, la même pour les 4 sommets
    void FlatQuad(int i, Vector3 center, Vector3 n)
    {
        Vector3 p = center + n * 0.25f;
        int px = (int)Math.Floor(p.x), py = (int)Math.Floor(p.y), pz = (int)Math.Floor(p.z);

        // Devant la face, un bloc opaque (sans lumière) : on lit la case derrière la face.
        // Ex. : la surface de l'eau sous un bloc posé dessus, éclairée par la case d'eau elle-même.
        if (BlockDatabase.IsOpaque(BlockAt(px, py, pz)))
        {
            p = center - n * 0.25f;
            px = (int)Math.Floor(p.x); py = (int)Math.Floor(p.y); pz = (int)Math.Floor(p.z);
        }

        int packed = LightAt(px, py, pz);
        byte blockLight = (byte)((packed & 15) * 17);
        byte skyLight = (byte)((packed >> 4) * 17);

        List<Color32> colors = buf.colors;
        for (int k = 0; k < 4; k++)
            colors[i + k] = new Color32(blockLight, skyLight, 255, colors[i + k].a);
    }

    readonly float[] cornerBrightness = new float[4];

    // Smooth lighting d'une face : pour chaque coin, les 4 cases qui le touchent devant la face
    //   F  = la case devant la face (côté centre de la face)
    //   S1, S2 = ses voisines le long des deux axes de la face, vers le coin
    //   C  = la case en diagonale, au coin
    // Lumière = moyenne des cases non opaques ; occlusion = nombre de cases opaques parmi S1, S2 et C
    // (S1 et S2 opaques : le coin est coincé, C ne compte plus, comme Minecraft).
    // Retourne false si la face ne s'y prête pas (case devant opaque) : elle sera éclairée à plat.
    bool SmoothQuad(int i, Vector3 center, Vector3 n)
    {
        // Axes de la face
        Vector3 t1, t2;
        float ax = Math.Abs(n.x), ay = Math.Abs(n.y), az = Math.Abs(n.z);
        if (ay >= ax && ay >= az) { t1 = Vector3.right; t2 = Vector3.forward; }
        else if (ax >= az)        { t1 = Vector3.up;    t2 = Vector3.forward; }
        else                      { t1 = Vector3.right; t2 = Vector3.up; }

        List<Vector3> verts = buf.vertices;
        List<Color32> colors = buf.colors;

        for (int k = 0; k < 4; k++)
        {
            Vector3 v = verts[i + k];
            float s1 = Vector3.Dot(v - center, t1) >= 0f ? 0.5f : -0.5f;  // vers le coin, le long de chaque axe
            float s2 = Vector3.Dot(v - center, t2) >= 0f ? 0.5f : -0.5f;
            Vector3 front = v + n * 0.25f;

            Vector3 f  = front - t1 * s1 - t2 * s2;
            Vector3 c1 = front + t1 * s1 - t2 * s2;
            Vector3 c2 = front - t1 * s1 + t2 * s2;
            Vector3 cc = front + t1 * s1 + t2 * s2;

            if (Opaque(f)) return false;
            bool o1 = Opaque(c1), o2 = Opaque(c2), oc = Opaque(cc);

            int ao = (o1 && o2) ? 0 : 3 - ((o1 ? 1 : 0) + (o2 ? 1 : 0) + (oc ? 1 : 0));

            int sumBlock = 0, sumSky = 0, samples = 0;
            AddLight(f, ref sumBlock, ref sumSky, ref samples);
            if (!o1) AddLight(c1, ref sumBlock, ref sumSky, ref samples);
            if (!o2) AddLight(c2, ref sumBlock, ref sumSky, ref samples);
            if (!oc && !(o1 && o2)) AddLight(cc, ref sumBlock, ref sumSky, ref samples);

            float block = sumBlock / (float)samples;
            float sky = sumSky / (float)samples;

            colors[i + k] = new Color32(
                (byte)Math.Round(block * 17f), (byte)Math.Round(sky * 17f),
                (byte)Math.Round(AoBrightness[ao] * 255f), colors[i + k].a);

            cornerBrightness[k] = AoBrightness[ao] * (Math.Max(block, sky) + 1f);
        }

        // La face est coupée en deux triangles par la diagonale 0-2. Si cette diagonale est la plus sombre, on la
        // retourne (diagonale 1-3) : sinon l'ombre d'un coin s'étirerait en traînée jusqu'au coin opposé.
        if (cornerBrightness[0] + cornerBrightness[2] < cornerBrightness[1] + cornerBrightness[3])
            buf.flipQuads.Add(i);

        return true;
    }

    // Une case d'un chunk voisin pas encore chargé est « inconnue » : ni opaque, ni comptée dans la moyenne
    // (sinon les coins au bord du monde chargé seraient assombris à tort)
    bool Opaque(Vector3 p)
    {
        int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y), z = (int)Math.Floor(p.z);
        return TryCell(x, y, z, out BlockType type, out _) && BlockDatabase.IsOpaque(type);
    }

    void AddLight(Vector3 p, ref int sumBlock, ref int sumSky, ref int samples)
    {
        int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y), z = (int)Math.Floor(p.z);
        if (!TryCell(x, y, z, out _, out _)) return;

        int packed = LightAt(x, y, z);
        sumBlock += packed & 15;
        sumSky += packed >> 4;
        samples++;
    }

    // Retourne la diagonale des faces marquées : (0,1,2)(0,2,3) devient (1,2,3)(1,3,0), même sens de rotation
    void FlipDarkDiagonals()
    {
        if (buf.flipQuads.Count == 0) return;

        List<int> tris = buf.triangles;
        for (int k = 0; k + 5 < tris.Count; k += 6)
        {
            int a = tris[k];
            if (!buf.flipQuads.Contains(a)) continue;
            if (tris[k + 1] != a + 1 || tris[k + 2] != a + 2 || tris[k + 3] != a || tris[k + 4] != a + 2 || tris[k + 5] != a + 3) continue;

            tris[k] = a + 1; tris[k + 1] = a + 2; tris[k + 2] = a + 3;
            tris[k + 3] = a + 1; tris[k + 4] = a + 3; tris[k + 5] = a;
        }
    }
}
