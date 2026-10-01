using UnityEngine;

// Pose de blocs contre la face visée, avec les règles de Minecraft : les herbes hautes se remplacent,
// et les dalles se posent en haut ou en bas, ou se complètent en bloc plein.
// (Partie de la classe World.)
public partial class World
{
    [Header("Pose de blocs")]
    [Tooltip("Écrit dans la Console pourquoi une pose est refusée (utile pour comprendre)")]
    [SerializeField] bool logPlacementFailures = true;

    // Pose un objet contre la face touchée d'un bloc. À UTILISER pour le clic de pose du joueur.
    //   hitBlock : le bloc visé (coordonnées monde)
    //   normal   : la face touchée (par exemple (0, 1, 0) pour le dessus)
    //   hitPoint : le point touché, en coordonnées MONDE (sert à choisir le haut ou le bas d'une dalle)
    // Renvoie true si le bloc a été posé : c'est alors à toi de retirer l'objet de l'inventaire.
    public bool TryPlaceAgainst(Vector3Int hitBlock, Vector3Int normal, Vector3 hitPoint, BlockType item)
    {
        // Viser un bloc remplaçable (herbe haute) : le nouveau bloc se pose À SA PLACE, comme dans Minecraft.
        // Sans ça, une herbe devant le sol détournerait la pose.
        bool replaceHit = BlockDatabase.Get(GetBlock(hitBlock.x, hitBlock.y, hitBlock.z)).replaceable;

        if (IsSlabItem(item, out BlockType top, out BlockType full))
            return TryPlaceSlab(hitBlock, normal, hitPoint, replaceHit, item, top, full);

        Vector3Int p = replaceHit ? hitBlock : hitBlock + normal;
        return PlaceAt(p, item, hitBlock, normal, hitPoint);
    }

    // Les objets qui se posent en dalle : l'objet est la dalle du BAS, avec ses deux variantes
    static bool IsSlabItem(BlockType item, out BlockType top, out BlockType full)
    {
        switch (item)
        {
            case BlockType.StoneSlab:
                top = BlockType.StoneSlabTop;
                full = BlockType.StoneDoubleSlab;
                return true;

            default:
                top = full = BlockType.Air;
                return false;
        }
    }

    bool TryPlaceSlab(Vector3Int hitBlock, Vector3Int normal, Vector3 hitPoint, bool replaceHit,
                      BlockType bottom, BlockType top, BlockType full)
    {
        BlockType hitType = GetBlock(hitBlock.x, hitBlock.y, hitBlock.z);

        // 1) On vise le dessus d'une dalle du bas, ou le dessous d'une dalle du haut :
        //    l'autre moitié de la case est libre, les deux dalles forment un bloc plein
        if ((hitType == bottom && normal.y == 1) || (hitType == top && normal.y == -1))
            return SetBlock(hitBlock.x, hitBlock.y, hitBlock.z, full);

        // 2) Case d'arrivée : celle de l'herbe visée, sinon la case voisine de la face touchée
        Vector3Int p = replaceHit ? hitBlock : hitBlock + normal;

        // Dalle du bas ou du haut : dessus d'un bloc = bas, dessous = haut ; sur un côté (ou à travers une herbe),
        // en haut si on vise la moitié haute de la case, en bas sinon
        BlockType placed;
        if (!replaceHit && normal.y == 1) placed = bottom;
        else if (!replaceHit && normal.y == -1) placed = top;
        else placed = (hitPoint.y - p.y) > 0.5f ? top : bottom;

        // La case contient déjà la dalle opposée : l'autre moitié est libre, on obtient un bloc plein
        BlockType existing = GetBlock(p.x, p.y, p.z);
        if ((existing == bottom && placed == top) || (existing == top && placed == bottom))
            return SetBlock(p.x, p.y, p.z, full);

        return PlaceAt(p, placed, hitBlock, normal, hitPoint);
    }

    // Pose un bloc dans une case, et explique dans la Console pourquoi si c'est refusé
    bool PlaceAt(Vector3Int p, BlockType type, Vector3Int hitBlock, Vector3Int normal, Vector3 hitPoint)
    {
        if (CanPlace(p.x, p.y, p.z, type))
            return SetBlock(p.x, p.y, p.z, type);

        if (logPlacementFailures)
        {
            string reason;
            if (p.y < 0 || p.y >= Chunk.SizeY)
                reason = "hors du monde";
            else if (!BlockDatabase.Get(GetBlock(p.x, p.y, p.z)).replaceable)
                reason = "la case est occupée par " + GetBlock(p.x, p.y, p.z);
            else
                reason = "il manque le support exigé par ce bloc";

            Debug.Log($"Pose de {type} refusée en {p} ({reason}). Bloc visé {hitBlock}, face {normal}, point {hitPoint}.");
        }

        return false;
    }
}
