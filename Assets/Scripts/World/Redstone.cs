using UnityEngine;

// Lecture des blocs, pour que les mêmes règles servent au monde (thread principal) et au mailleur (autre thread).
public interface IBlockView
{
    BlockType TypeAt(int x, int y, int z);
    byte StateAt(int x, int y, int z);
}

// Identifiants et règles PURES de la redstone : aucune dépendance au monde, donc testables et utilisables par
// le mailleur. La simulation du signal est dans World.Redstone.cs.
//
// Les blocs ont des numéros fixes (écrits dans les sauvegardes). Les composants qui se fixent à un bloc
// (torche, levier, bouton) existent en 5 variantes : posés au sol, ou contre un mur au nord / sud / est / ouest.
public static class Redstone
{
    // Numéros des blocs (BlockType)
    public const int Ore = 64, Block = 65, Dust = 66, Lamp = 67, LampLit = 68;
    public const int TorchLit = 69;        // 69 à 73
    public const int TorchUnlit = 74;      // 74 à 78
    public const int LeverOff = 79;        // 79 à 83
    public const int LeverOn = 84;         // 84 à 88
    public const int ButtonOff = 89;       // 89 à 93
    public const int ButtonPressed = 94;   // 94 à 98
    public const int Repeater = 99;

    // Un fil donne au plus ce niveau de signal ; il perd 1 niveau par bloc
    public const int MaxPower = 15;

    // Orientation d'un composant fixé : 0 = posé au sol, 1 = mur au nord (+Z), 2 = mur au sud (-Z),
    // 3 = mur à l'est (+X), 4 = mur à l'ouest (-X)
    public static readonly Vector3Int[] AttachDirs =
    {
        new Vector3Int(0, -1, 0), new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1), new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
    };

    // Directions horizontales : 0 = nord (+Z), 1 = est (+X), 2 = sud (-Z), 3 = ouest (-X)
    public static readonly Vector3Int[] Horizontal =
    {
        new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, -1), new Vector3Int(-1, 0, 0),
    };

    public static readonly Vector3Int[] AllDirs =
    {
        new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 1, 0),
        new Vector3Int(0, -1, 0), new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
    };

    // ------------------------------------------------------------------
    // Familles de blocs
    // ------------------------------------------------------------------

    public static bool IsDust(BlockType t) => (int)t == Dust;
    public static bool IsRepeater(BlockType t) => (int)t == Repeater;

    public static bool IsLamp(BlockType t, out bool lit)
    {
        lit = (int)t == LampLit;
        return (int)t == Lamp || lit;
    }

    public static bool IsTorch(BlockType t, out bool lit, out int orient) => InFamily(t, TorchLit, TorchUnlit, out lit, out orient);
    public static bool IsLever(BlockType t, out bool on, out int orient) => InFamily(t, LeverOn, LeverOff, out on, out orient);
    public static bool IsButton(BlockType t, out bool pressed, out int orient) => InFamily(t, ButtonPressed, ButtonOff, out pressed, out orient);

    // `activeBase` : premier numéro de la famille allumée / enfoncée ; `idleBase` : celui de la famille éteinte
    static bool InFamily(BlockType t, int activeBase, int idleBase, out bool active, out int orient)
    {
        int v = (int)t;
        if (v >= activeBase && v < activeBase + 5) { active = true; orient = v - activeBase; return true; }
        if (v >= idleBase && v < idleBase + 5) { active = false; orient = v - idleBase; return true; }
        active = false;
        orient = 0;
        return false;
    }

    public static BlockType Torch(bool lit, int orient) => (BlockType)((lit ? TorchLit : TorchUnlit) + orient);
    public static BlockType Lever(bool on, int orient) => (BlockType)((on ? LeverOn : LeverOff) + orient);
    public static BlockType Button(bool pressed, int orient) => (BlockType)((pressed ? ButtonPressed : ButtonOff) + orient);

    // Quel composant est-ce, et peut-on le relier à un fil ?
    public static bool IsPowerComponent(BlockType t)
    {
        int v = (int)t;
        return v == Block || (v >= TorchLit && v < ButtonPressed + 5) && v != Repeater;
    }

    // Orientation d'un composant posé contre la face `normal` d'un bloc : au sol, ou sur le côté.
    // -1 = impossible (au plafond).
    public static int OrientFromNormal(Vector3Int normal)
    {
        if (normal.y > 0) return 0;
        if (normal.y < 0) return -1;
        if (normal.z < 0) return 1;   // la face regarde le sud : le mur est au nord de la case
        if (normal.z > 0) return 2;
        if (normal.x < 0) return 3;
        return 4;
    }

    // Direction horizontale (0 = nord, 1 = est, 2 = sud, 3 = ouest) d'un angle de caméra en degrés (0 = nord)
    public static int DirFromYaw(float yawDegrees)
    {
        int d = Mathf.RoundToInt(yawDegrees / 90f) % 4;
        return d < 0 ? d + 4 : d;
    }

    // ------------------------------------------------------------------
    // Répéteur : l'état tient dans un octet
    //   bits 0-1 : sens de sortie (0 à 3, voir Horizontal) ; bits 2-3 : délai (0 à 3 = 1 à 4 tics) ; bit 4 : allumé
    // ------------------------------------------------------------------

    public static int RepeaterFacing(byte s) => s & 3;
    public static int RepeaterDelay(byte s) => (s >> 2) & 3;
    public static bool RepeaterPowered(byte s) => ((s >> 4) & 1) != 0;
    public static byte RepeaterState(int facing, int delay, bool powered) => (byte)((facing & 3) | ((delay & 3) << 2) | (powered ? 16 : 0));

    // ------------------------------------------------------------------
    // Forme d'un fil : comment il se raccorde à ses 4 voisins horizontaux
    // ------------------------------------------------------------------

    // Un bloc plein et opaque : le signal ne le traverse pas, un fil peut grimper dessus
    public static bool IsSolid(BlockType t) => BlockDatabase.IsOpaque(t);

    // Les composants auxquels un fil se raccorde (sur le côté `dir`, 0 à 3)
    static bool ConnectsAsComponent(BlockType t, byte state, int dir)
    {
        if (IsPowerComponent(t)) return true;
        // Un répéteur se raccorde par son entrée et sa sortie seulement : dans son axe
        return IsRepeater(t) && (RepeaterFacing(state) & 1) == (dir & 1);
    }

    // Lien du fil en (x, y, z) vers son voisin du côté `dir`, comme dans Minecraft :
    //   - monter : un fil sur le bloc plein voisin, si le dessus de ce fil-ci est libre ;
    //   - à plat : un fil voisin, ou un composant ;
    //   - descendre : un fil sous la case voisine, si cette case n'est pas un bloc plein.
    // Retourne 0 = aucun, 1 = à plat (ou en descente), 2 = en montée. `ty` : hauteur de la case liée,
    // `toWire` : c'est un fil (le signal passe), sinon un composant (simple raccord).
    public static int Link(IBlockView v, int x, int y, int z, int dir, out int ty, out bool toWire)
    {
        Vector3Int d = Horizontal[dir];
        int nx = x + d.x, nz = z + d.z;
        ty = y;
        toWire = false;

        BlockType n = v.TypeAt(nx, y, nz);
        bool aboveFree = !IsSolid(v.TypeAt(x, y + 1, z));

        if (aboveFree && IsSolid(n) && IsDust(v.TypeAt(nx, y + 1, nz)))
        {
            ty = y + 1;
            toWire = true;
            return 2;
        }
        if (IsDust(n))
        {
            toWire = true;
            return 1;
        }
        if (ConnectsAsComponent(n, v.StateAt(nx, y, nz), dir)) return 1;
        if (!IsSolid(n) && IsDust(v.TypeAt(nx, y - 1, nz)))
        {
            ty = y - 1;
            toWire = true;
            return 1;
        }
        return 0;
    }

    // Raccords visibles du fil : conn[0..3] = 0 rien, 1 à plat, 2 monte sur le bloc voisin.
    // Un fil raccordé d'un seul côté se prolonge de l'autre (ligne droite) ; sans aucun raccord, c'est un point.
    public static void WireShape(IBlockView v, int x, int y, int z, int[] conn)
    {
        for (int i = 0; i < 4; i++) conn[i] = Link(v, x, y, z, i, out _, out _);

        bool northSouth = conn[0] > 0 || conn[2] > 0;
        bool eastWest = conn[1] > 0 || conn[3] > 0;

        if (northSouth && !eastWest)
        {
            if (conn[0] == 0) conn[0] = 1;
            if (conn[2] == 0) conn[2] = 1;
        }
        else if (eastWest && !northSouth)
        {
            if (conn[1] == 0) conn[1] = 1;
            if (conn[3] == 0) conn[3] = 1;
        }
    }
}
