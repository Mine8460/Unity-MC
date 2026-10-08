using UnityEngine;

// Comment un bloc peut être tourné à la pose. Réglé dans le fichier du bloc (BlockDefinition > Orientation).
// L'orientation est stockée dans l'état du bloc (0 = tel que dessiné) : UN SEUL fichier de bloc suffit,
// pas besoin d'un bloc par direction.
public enum Orientation : byte
{
    None,        // jamais tourné
    Horizontal,  // 4 directions selon où regarde le joueur (four, coffre, escalier...). Dessine l'AVANT du bloc vers le NORD (+Z) : il fera face au joueur.
    Axis,        // 3 axes selon la face visée (bûche, pilier). Dessine le bloc DEBOUT (axe Y).
    Facing,      // 6 directions : le HAUT du bloc dessiné pointe vers la face visée (torche murale, levier, tuyau...)
}

public static class BlockOrientation
{
    // Faces : 0 = haut, 1 = bas, 2 = +Z, 3 = -Z, 4 = +X, 5 = -X (comme partout dans le mesher)
    static readonly Vector3[] Dirs =
    {
        Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left,
    };

    // [orientation][état] -> images des trois axes (arrondies : une rotation de 90° est exacte)
    static readonly Vector3[,][] bases = BuildBases();

    static Vector3[,][] BuildBases()
    {
        var result = new Vector3[4, 6][];
        for (int o = 1; o < 4; o++)
        for (int s = 0; s < 6; s++)
        {
            Quaternion q = Rot((Orientation)o, s);
            result[o, s] = new[] { Round(q * Vector3.right), Round(q * Vector3.up), Round(q * Vector3.forward) };
        }
        return result;
    }

    static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x), Mathf.Round(v.y), Mathf.Round(v.z));

    static Quaternion Rot(Orientation o, int s)
    {
        switch (o)
        {
            case Orientation.Horizontal:
                return Quaternion.Euler(0f, 90f * (s & 3), 0f);

            case Orientation.Axis:
                if (s == 1) return Quaternion.Euler(0f, 0f, -90f);   // couché le long de X
                if (s == 2) return Quaternion.Euler(90f, 0f, 0f);    // couché le long de Z
                return Quaternion.identity;

            case Orientation.Facing:
                switch (s)
                {
                    case 1: return Quaternion.Euler(180f, 0f, 0f);
                    case 2: return Quaternion.Euler(90f, 0f, 0f);
                    case 3: return Quaternion.Euler(-90f, 0f, 0f);
                    case 4: return Quaternion.Euler(0f, 0f, -90f);
                    case 5: return Quaternion.Euler(0f, 0f, 90f);
                    default: return Quaternion.identity;
                }
        }
        return Quaternion.identity;
    }

    public static int StateCount(Orientation o)
    {
        switch (o)
        {
            case Orientation.Horizontal: return 4;
            case Orientation.Axis: return 3;
            case Orientation.Facing: return 6;
            default: return 1;
        }
    }

    // Un état valide pour cette orientation (sinon 0)
    public static int Clamp(Orientation o, int state) => state >= 0 && state < StateCount(o) ? state : 0;

    // Tourne un VECTEUR (direction, normale)
    public static Vector3 RotateDir(Orientation o, int state, Vector3 v)
    {
        Vector3[] b = bases[(int)o, state];
        return b[0] * v.x + b[1] * v.y + b[2] * v.z;
    }

    // Tourne un POINT local au bloc (0..1) autour du centre du bloc
    public static Vector3 RotateLocal(Orientation o, int state, Vector3 p)
    {
        Vector3 c = new Vector3(0.5f, 0.5f, 0.5f);
        return c + RotateDir(o, state, p - c);
    }

    // La face « source » f (celle du bloc dessiné) se retrouve sur quelle face du monde ?
    public static int RotateFace(Orientation o, int state, int f)
    {
        Vector3 d = RotateDir(o, state, Dirs[f]);
        for (int i = 0; i < 6; i++)
            if ((Dirs[i] - d).sqrMagnitude < 0.01f) return i;
        return f;
    }

    public static Box[] RotateBoxes(Box[] boxes, Orientation o, int state)
    {
        if (boxes == null || state == 0) return boxes;

        var result = new Box[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            Vector3 a = RotateLocal(o, state, boxes[i].min);
            Vector3 b = RotateLocal(o, state, boxes[i].max);
            result[i] = new Box(Vector3.Min(a, b), Vector3.Max(a, b));
        }
        return result;
    }

    // État à la pose. normal = face visée (normale), yaw = direction du regard du joueur en degrés.
    public static byte StateFromPlacement(Orientation o, Vector3Int normal, float yaw)
    {
        switch (o)
        {
            case Orientation.Horizontal:
                return (byte)Redstone.DirFromYaw(yaw + 180f); // l'avant du bloc regarde le joueur

            case Orientation.Axis:
                if (normal.x != 0) return 1;
                if (normal.z != 0) return 2;
                return 0;

            case Orientation.Facing:
                if (normal.y > 0) return 0;
                if (normal.y < 0) return 1;
                if (normal.z > 0) return 2;
                if (normal.z < 0) return 3;
                return (byte)(normal.x > 0 ? 4 : 5);
        }
        return 0;
    }
}
