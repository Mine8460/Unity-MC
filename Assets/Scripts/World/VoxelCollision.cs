using UnityEngine;

// Collision d'une boîte alignée sur les axes contre les boîtes de collision des blocs
// (la même que celle du joueur, utilisable par n'importe quelle entité).
public static class VoxelCollision
{
    // Marge minuscule : la boîte est testée légèrement rétrécie, pour ignorer les simples contacts
    const float Skin = 0.0001f;

    // Teste une boîte de largeur `width` et de hauteur `height`, dont les PIEDS sont en p.
    // Si axis >= 0 (0 = x, 1 = y, 2 = z), calcule aussi `snap` : la position sur cet axe où coller la boîte
    // pour la débloquer, selon le sens du déplacement `dir`.
    public static bool Collide(World world, Vector3 p, float width, float height, int axis, float dir, out float snap)
    {
        float hw = width * 0.5f;
        Vector3 min = new Vector3(p.x - hw, p.y, p.z - hw);
        Vector3 max = new Vector3(p.x + hw, p.y + height, p.z + hw);

        int x0 = Mathf.FloorToInt(min.x + Skin), x1 = Mathf.FloorToInt(max.x - Skin);
        int y0 = Mathf.FloorToInt(min.y + Skin), y1 = Mathf.FloorToInt(max.y - Skin);
        int z0 = Mathf.FloorToInt(min.z + Skin), z1 = Mathf.FloorToInt(max.z - Skin);

        bool hit = false;
        snap = dir > 0f ? float.PositiveInfinity : float.NegativeInfinity;

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            Box[] boxes = BlockDatabase.Get(world.GetBlock(x, y, z)).collisionBoxes;
            if (boxes == null) continue;

            var cell = new Vector3(x, y, z);
            for (int i = 0; i < boxes.Length; i++)
            {
                Vector3 bmin = cell + boxes[i].min;
                Vector3 bmax = cell + boxes[i].max;

                if (min.x + Skin >= bmax.x || max.x - Skin <= bmin.x) continue;
                if (min.y + Skin >= bmax.y || max.y - Skin <= bmin.y) continue;
                if (min.z + Skin >= bmax.z || max.z - Skin <= bmin.z) continue;

                hit = true;
                if (axis < 0) return true;

                float candidate;
                if (dir > 0f)
                    candidate = bmin[axis] - (axis == 1 ? height : hw);
                else
                    candidate = bmax[axis] + (axis == 1 ? 0f : hw);

                snap = dir > 0f ? Mathf.Min(snap, candidate) : Mathf.Max(snap, candidate);
            }
        }

        if (!hit) snap = 0f;
        return hit;
    }
}
