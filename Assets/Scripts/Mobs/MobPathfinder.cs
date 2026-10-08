using System.Collections.Generic;
using UnityEngine;

// Recherche de chemin pour les monstres (A* sur la grille de blocs).
// Une case « où se tenir » = deux cases libres (corps) au-dessus d'un sol plein.
// Déplacements : marcher, monter d'un bloc (en sautant, s'il y a la place), descendre de 3 blocs au plus.
public static class MobPathfinder
{
    class Node
    {
        public int x, y, z;
        public float g, f;
        public Node parent;
        public int heapIndex = -1;
    }

    static readonly int[] DX = { 1, -1, 0, 0 };
    static readonly int[] DZ = { 0, 0, 1, -1 };

    // Cache d'une recherche (cases libres / pleines)
    static readonly Dictionary<long, bool> freeCache = new Dictionary<long, bool>();
    static readonly Dictionary<long, bool> solidCache = new Dictionary<long, bool>();
    static readonly Dictionary<long, Node> nodes = new Dictionary<long, Node>();
    static readonly List<Node> heap = new List<Node>();
    static World cacheWorld;

    // Taille du monstre en cases : hh = hauteur, [fx0, fx1] = étendue autour de la case centrale (0 = une seule case)
    static int hh = 2, fx0 = 0, fx1 = 0;

    static void SetSize(float width, float height)
    {
        hh = Mathf.Max(1, Mathf.CeilToInt(height - 0.001f));
        float half = Mathf.Max(width, 0.1f) * 0.5f;
        fx0 = -Mathf.Max(0, Mathf.CeilToInt(half - 0.5f - 0.001f));   // cases en plus à gauche du centre
        fx1 = -fx0;
    }

    // La boîte du monstre, posée sur la case (x, y, z), est-elle entièrement libre ?
    static bool FreeBox(World w, int x, int y, int z)
    {
        for (int dx = fx0; dx <= fx1; dx++)
        for (int dz = fx0; dz <= fx1; dz++)
        for (int dy = 0; dy < hh; dy++)
            if (!Free(w, x + dx, y + dy, z + dz)) return false;
        return true;
    }

    static long Key(int x, int y, int z) { return ((long)(x + 1048576) << 42) | ((long)(z + 1048576) << 21) | (long)(y & 0x1FFFFF); }

    // Case traversable : ni solide ni liquide
    static bool Free(World w, int x, int y, int z)
    {
        long k = Key(x, y, z);
        bool v;
        if (freeCache.TryGetValue(k, out v)) return v;
        BlockInfo i = BlockDatabase.Get(w.GetBlock(x, y, z));
        v = !i.collidable && i.shape != BlockShape.Liquid;
        freeCache[k] = v;
        return v;
    }

    static bool Solid(World w, int x, int y, int z)
    {
        long k = Key(x, y, z);
        bool v;
        if (solidCache.TryGetValue(k, out v)) return v;
        v = BlockDatabase.Get(w.GetBlock(x, y, z)).collidable;
        solidCache[k] = v;
        return v;
    }

    public static bool Standable(World w, int x, int y, int z)
    {
        return y > 0 && FreeBox(w, x, y, z) && Solid(w, x, y - 1, z);
    }

    // Où peut-on aller depuis (x, y, z) en direction (dx, dz) ? Renvoie la case d'arrivée (y différent si on monte ou descend).
    public static bool Step(World w, int x, int y, int z, int dx, int dz, float width, float height, out int ty)
    {
        SetSize(width, height);
        int tx = x + dx, tz = z + dz;
        ty = y;

        // Marcher ou descendre (jusqu'à 3 blocs)
        if (FreeBox(w, x, y, z) && FreeBox(w, tx, y, tz))
        {
            for (int yy = y; yy >= y - 3; yy--)
            {
                if (!FreeBox(w, tx, yy, tz)) break;
                if (Solid(w, tx, yy - 1, tz)) { ty = yy; return true; }
            }
        }

        // Monter d'un bloc : de la place au-dessus de nous pour sauter, et la case d'arrivée est libre
        if (FreeBox(w, x, y + 1, z) && FreeBox(w, tx, y + 1, tz) && Solid(w, tx, y, tz))
        {
            ty = y + 1;
            return true;
        }
        return false;
    }

    // Chemin de `start` jusqu'à côté de `goal` (cases entières). Renvoie null si le joueur est inatteignable.
    // La liste contient les centres des cases à suivre (le départ n'est pas inclus).
    public static List<Vector3> Find(World w, Vector3Int start, Vector3Int goal, float maxRange, int maxNodes, float width, float height)
    {
        SetSize(width, height);
        if (cacheWorld != w) { cacheWorld = w; }
        freeCache.Clear(); solidCache.Clear(); nodes.Clear(); heap.Clear();

        var s = new Node { x = start.x, y = start.y, z = start.z, g = 0f };
        s.f = H(s, goal);
        nodes[Key(s.x, s.y, s.z)] = s;
        Push(s);

        float range2 = maxRange * maxRange;
        int expanded = 0;
        Node found = null;

        while (heap.Count > 0 && expanded < maxNodes)
        {
            Node cur = Pop();
            expanded++;

            if (Mathf.Abs(cur.x - goal.x) + Mathf.Abs(cur.z - goal.z) <= 1 && Mathf.Abs(cur.y - goal.y) <= 1) { found = cur; break; }

            for (int d = 0; d < 4; d++)
            {
                int ty;
                if (!Step(w, cur.x, cur.y, cur.z, DX[d], DZ[d], width, height, out ty)) continue;
                int nx = cur.x + DX[d], nz = cur.z + DZ[d];
                if ((nx - start.x) * (nx - start.x) + (nz - start.z) * (nz - start.z) > range2) continue;

                float cost = 1f + (ty != cur.y ? 0.6f : 0f);
                float ng = cur.g + cost;
                long k = Key(nx, ty, nz);
                Node n;
                if (nodes.TryGetValue(k, out n))
                {
                    if (ng >= n.g) continue;
                    n.g = ng; n.f = ng + H(n, goal); n.parent = cur;
                    if (n.heapIndex >= 0) SiftUp(n.heapIndex); else Push(n);
                }
                else
                {
                    n = new Node { x = nx, y = ty, z = nz, g = ng, parent = cur };
                    n.f = ng + H(n, goal);
                    nodes[k] = n;
                    Push(n);
                }
            }
        }

        if (found == null) return null;

        var path = new List<Vector3>();
        for (Node n = found; n != null && n.parent != null; n = n.parent)
            path.Add(new Vector3(n.x + 0.5f, n.y, n.z + 0.5f));
        path.Reverse();
        return path;
    }

    static float H(Node n, Vector3Int g)
    {
        return Mathf.Abs(n.x - g.x) + Mathf.Abs(n.z - g.z) + Mathf.Abs(n.y - g.y) * 0.5f;
    }

    // Tas binaire (plus petit f en haut)
    static void Push(Node n) { n.heapIndex = heap.Count; heap.Add(n); SiftUp(n.heapIndex); }

    static Node Pop()
    {
        Node top = heap[0];
        Node last = heap[heap.Count - 1];
        heap.RemoveAt(heap.Count - 1);
        top.heapIndex = -1;
        if (heap.Count > 0)
        {
            heap[0] = last; last.heapIndex = 0;
            SiftDown(0);
        }
        return top;
    }

    static void SiftUp(int i)
    {
        Node n = heap[i];
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (heap[p].f <= n.f) break;
            heap[i] = heap[p]; heap[i].heapIndex = i;
            i = p;
        }
        heap[i] = n; n.heapIndex = i;
    }

    static void SiftDown(int i)
    {
        Node n = heap[i];
        int count = heap.Count;
        while (true)
        {
            int c = i * 2 + 1;
            if (c >= count) break;
            if (c + 1 < count && heap[c + 1].f < heap[c].f) c++;
            if (heap[c].f >= n.f) break;
            heap[i] = heap[c]; heap[i].heapIndex = i;
            i = c;
        }
        heap[i] = n; n.heapIndex = i;
    }

    // Case où se tient un joueur/monstre : on descend jusqu'au sol (4 blocs au plus)
    public static bool GroundCell(World w, Vector3 pos, float width, float height, out Vector3Int cell)
    {
        SetSize(width, height);
        freeCache.Clear(); solidCache.Clear();
        int x = Mathf.FloorToInt(pos.x), z = Mathf.FloorToInt(pos.z), y = Mathf.FloorToInt(pos.y + 0.05f);
        for (int i = 0; i <= 4; i++)
        {
            if (Standable(w, x, y - i, z)) { cell = new Vector3Int(x, y - i, z); return true; }
        }
        cell = new Vector3Int(x, y, z);
        return false;
    }
}
