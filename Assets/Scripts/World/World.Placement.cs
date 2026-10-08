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
        // Clic droit sur un levier, un bouton ou un répéteur : on l'utilise, on ne pose rien (Maj : on pose un bloc contre lui).
        // Retourne false : aucun objet n'est consommé.
        bool sneaking = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        // (Le clic droit lui-même est géré par BlockUse, même la main vide : ici on empêche seulement de poser un bloc.)
        if (!sneaking && IsInteractive(hitBlock)) return false;

        bool replaceHit = BlockDatabase.Get(GetBlock(hitBlock.x, hitBlock.y, hitBlock.z)).replaceable;

        // Redstone : fil, répéteur, et composants qui se fixent au sol ou à un mur
        // (Désactivé : torches, leviers et boutons passent par « Wall Variant » et « Orientation » dans les fichiers de blocs.
        //  La poussière se pose comme un bloc normal ; le répéteur est géré plus bas.)
        //if (TryPlaceRedstone(hitBlock, normal, hitPoint, replaceHit, item, out bool redstonePlaced))
        //    return redstonePlaced;

        if (IsSlabItem(item, out BlockType top, out BlockType full))
            return TryPlaceSlab(hitBlock, normal, hitPoint, replaceHit, item, top, full);

        Vector3Int p = replaceHit ? hitBlock : hitBlock + normal;

        Vector3Int n = replaceHit ? Vector3Int.up : normal;
        BlockType placeType = item;
        byte orientState = 0;

        // Variante murale (torche, levier, bouton...) : au sol c'est le bloc de l'objet, contre un mur c'est sa
        // variante murale, tournée vers l'extérieur du mur ; jamais au plafond.
        BlockType wall = BlockDatabase.GetRef(item).wallVariant;
        if ((int)item == Redstone.Repeater)
        {
            // Répéteur : la sortie regarde loin du joueur
            orientState = Redstone.RepeaterState(Redstone.DirFromYaw(LookYaw()), 0, false);
        }
        else if (wall != BlockType.Air)
        {
            if (n.y < 0) return false;
            if (n.y == 0)
            {
                placeType = wall;
                orientState = (byte)(n.z > 0 ? 0 : n.x > 0 ? 1 : n.z < 0 ? 2 : 3); // index dans Redstone.Horizontal
            }
        }
        else
        {
            // Bloc orientable : un seul fichier, l'orientation vient de la face visée et du regard du joueur
            Orientation orientation = BlockDatabase.GetRef(item).orientation;
            if (orientation != Orientation.None)
                orientState = BlockOrientation.StateFromPlacement(orientation, n, LookYaw());
        }

        return PlaceAt(p, placeType, hitBlock, normal, hitPoint, orientState);
    }

    // Direction du regard du joueur, en degrés (le corps ne tourne pas : on lit le PlayerController)
    float LookYaw()
    {
        if (lookController == null && player != null) lookController = player.GetComponentInParent<PlayerController>();
        if (lookController != null) return lookController.Yaw;
        return Camera.main != null ? Camera.main.transform.eulerAngles.y : 0f;
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

    // Pose des composants de redstone. Retourne true si l'objet en est un (alors `placed` dit si la pose a réussi).
    //   - la poussière se pose sur un bloc plein ; le répéteur aussi, tourné dans le sens où le joueur regarde ;
    //   - la torche, le levier et le bouton se posent au sol ou contre un mur selon la face visée (jamais au plafond).
    PlayerController lookController;

    bool TryPlaceRedstone(Vector3Int hitBlock, Vector3Int normal, Vector3 hitPoint, bool replaceHit, BlockType item, out bool placed)
    {
        placed = false;
        int id = (int)item;
        BlockType type;
        byte state = 0;

        if (id == Redstone.Dust)
        {
            type = item;
        }
        else if (id == Redstone.Repeater)
        {
            type = item;
            int facing = Redstone.DirFromYaw(LookYaw()); // la sortie regarde loin du joueur
            state = Redstone.RepeaterState(facing, 0, false);
        }
        else if (id == Redstone.TorchLit || id == Redstone.LeverOff || id == Redstone.ButtonOff)
        {
            int orient = Redstone.OrientFromNormal(replaceHit ? Vector3Int.up : normal);
            if (orient < 0) return true; // pas au plafond

            if (id == Redstone.TorchLit) type = Redstone.Torch(true, orient);
            else if (id == Redstone.LeverOff) type = Redstone.Lever(false, orient);
            else type = Redstone.Button(false, orient);
        }
        else
        {
            return false;
        }

        Vector3Int p = replaceHit ? hitBlock : hitBlock + normal;
        placed = PlaceAt(p, type, hitBlock, normal, hitPoint, state);
        return true;
    }

    // Pose un bloc dans une case, et explique dans la Console pourquoi si c'est refusé
    bool PlaceAt(Vector3Int p, BlockType type, Vector3Int hitBlock, Vector3Int normal, Vector3 hitPoint, byte state = 0)
    {
        if (CanPlace(p.x, p.y, p.z, type, state))
            return SetBlockAndState(p.x, p.y, p.z, type, state, false);

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
