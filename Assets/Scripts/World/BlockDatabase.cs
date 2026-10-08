using System.Collections.Generic;
using UnityEngine;

// Types des blocs. (L'enum BlockType, les numéros des blocs utilisés par le code, est dans BlockType.cs.)

public enum BlockShape : byte
{
    Cube,   // cube plein (feuilles et vitre en font partie)
    Cross,  // deux plans en X, double face (herbes hautes, fleurs...)
    Model,  // assemblage de boîtes (dalle, enclume, torche...) ou modèle Blockbench
    Liquid, // eau : transparente, sans collision, dessinée avec son propre matériau
    Wire,     // poussière de redstone : fil plat posé au sol, dont les branches suivent ses connexions
    Repeater, // répéteur de redstone : dalle basse orientée, avec ses deux torches
}

// Comment le shader éclaire un bloc (la valeur est écrite dans l'alpha des couleurs de sommets)
public enum LightMode : byte
{
    Pixel = 0,         // pixels d'ombre alignés sur la texture (cubes, modèles)
    Block = 128,       // une seule ombre par bloc, lue au-dessus du bloc (plantes en diagonale)
    Wire = 160,        // redstone : comme « Block », teintée selon la puissance (lue dans le canal bleu des couleurs)
    FullBright = 255,  // ni lumière ni ombre : couleurs exactes de la texture (torche)
}

// Boîte alignée sur les axes, en coordonnées de bloc (0..1)
public readonly struct Box
{
    public readonly Vector3 min, max;
    public Box(Vector3 min, Vector3 max) { this.min = min; this.max = max; }
}

// Une boîte d'un modèle. Coordonnées en blocs (0..1).
// tiles : 6 entrées = haut, bas, +Z, -Z, +X, -X  (-1 = pas de face)
public readonly struct Element
{
    public readonly Vector3 min, max;
    public readonly int[] tiles;
    public Element(Vector3 min, Vector3 max, int[] tiles)
    {
        this.min = min;
        this.max = max;
        this.tiles = tiles;
    }
}

// Un quad de modèle importé, déjà converti en repère Unity (coordonnées de bloc 0..1).
// Les sommets sont dans le bon ordre pour les triangles (0,1,2) et (0,2,3).
public struct ModelQuad
{
    public Vector3 p0, p1, p2, p3;
    public Vector2 uv0, uv1, uv2, uv3;   // UV d'atlas finales
    public Vector3 normal;               // direction axiale de la face (éclairage)
    public int cullFace;                 // 0..5 = voisin qui peut la cacher, -1 = jamais
}

// Comment calculer la collision d'un modèle importé
public enum ModelCollision
{
    None,       // traversable
    Elements,   // une boîte par élément du modèle (boîtes englobantes si tourné)
    FullBlock,  // un cube plein
}

// Ce dont un bloc a besoin pour exister
public enum SupportRule : byte
{
    None,           // aucun
    SolidBelow,     // un cube plein et solide sous lui (torche)
    SoilBelow,      // de l'herbe ou de la terre sous lui (plantes)
    SolidAttached,  // un cube plein et solide dans la direction attachDir (torche murale)
    OpaqueBelow,    // un cube plein et OPAQUE sous lui (poussière de redstone, répéteur)
}

public struct BlockInfo
{
    public BlockShape shape;
    public bool hasMesh;        // false pour l'air : rien à dessiner
    public bool opaque;         // true = cache les faces des blocs voisins
    public bool collidable;
    public bool cullSameType;   // pas de face entre deux blocs identiques (vitre, dalle...)
    public bool randomOffset;   // décalage aléatoire par position (herbes hautes)
    public SupportRule support; // ce dont le bloc a besoin pour exister
    public Vector3Int attachDir;// pour SolidAttached : direction du bloc qui le soutient
    public bool gravity;        // tombe quand il n'a plus rien dessous (enclume)
    public bool replaceable;    // peut être remplacé par un autre bloc (air, herbe, torche)
    public bool isSoil;         // les plantes peuvent y pousser (herbe, terre)
    public byte emission;       // lumière émise, de 0 à 15 (torche : 14)
    public byte lightFilter;    // lumière absorbée en traversant le bloc (feuilles : 1). Un bloc opaque bloque tout.
    public float breakTime;     // temps de casse à la main, en secondes (0 = instantané, négatif = incassable)
    public ToolKind tool;       // outil qui accélère la casse (pioche, hache, pelle)
    public int harvestLevel;    // niveau d'outil exigé pour que le bloc lâche quelque chose
    public ItemType dropItem;   // objet lâché à la place du bloc (minerai de charbon -> charbon) ; None = le bloc
    public bool dropsNothing;       // ne lâche rien quand il est cassé (feuilles, vitre, herbe haute)
    public BlockType dropOverride;  // lâche un autre bloc (l'herbe lâche de la terre) ; Air = le bloc lui-même
    public byte dropCount;          // nombre d'objets lâchés (0 = un seul)
    public byte dropCountRandom;    // en plus : de 0 à ce nombre d'objets, au hasard (minerai de redstone : 4 + 0 à 1)
    public byte flushMask;          // faces (bit 0 = haut, 1 = bas, 2 = +Z, 3 = -Z, 4 = +X, 5 = -X) contre le bord du bloc
    public LightMode lightMode; // éclairage par le shader (Pixel par défaut)
    public Orientation orientation; // le bloc peut être tourné à la pose (état du bloc)
    public BlockType wallVariant;   // bloc posé à sa place contre un mur (Air = aucun)
    public int tileTop, tileBottom, tileSide;
    public int[] faceTiles;   // 6 tuiles : 0 haut, 1 bas, 2 +Z avant, 3 -Z arrière, 4 +X, 5 -X   // tuiles de l'atlas (cubes et plantes)
    public Element[] elements;                  // forme « Model » en boîtes
    public ModelQuad[] quads;                   // modèles importés (Blockbench)
    public Box[] collisionBoxes;                // null = aucune collision
    public Box[] selectionBoxes;                // zone visée par le raycast (casser/poser), même sans collision
}

// Tous les blocs, chargés au lancement depuis les fichiers Assets/Resources/Blocks/*.asset (BlockDefinition).
// L'atlas de textures est assemblé ici, à partir des images glissées dans ces fichiers.
// Tout est prêt avant le démarrage des threads de génération : ensuite, tout est en lecture seule.
public static class BlockDatabase
{
    public const string ResourcesFolder = "Blocks";

    // Marge UV en fraction de pixel de texture. DOIT être identique à "uvInset" dans PixelShadowLit.shader
    public const float UvInsetTexels = 0.01f;

    // Atlas assemblé au lancement : N x N tuiles de TilePixels pixels
    public static int AtlasTilesPerRow { get; private set; } = 1;
    static bool[] translucentTiles = new bool[0];
    public static bool HasTranslucentTiles { get; private set; }
    // Tuile avec des pixels partiellement transparents (ni opaques ni totalement transparents)
    public static bool IsTranslucentTile(int tile) => tile >= 0 && tile < translucentTiles.Length && translucentTiles[tile];
    public static int TilePixels { get; private set; } = 16;
    public static Texture2D Atlas { get; private set; }
    public static Texture2D HeightAtlas { get; private set; }     // null si aucun bloc n'a de height map
    public static Texture2D MetallicAtlas { get; private set; }   // null si aucun bloc n'a de metallic map
    public static Texture2D EmissionAtlas { get; private set; }   // null si aucun bloc n'a d'émissive

    static readonly BlockInfo[] infos = new BlockInfo[256];
    static readonly string[] names = new string[256];

    static readonly Box[] FullBox = { new Box(Vector3.zero, Vector3.one) };
    static readonly Box[] PlantBox = { new Box(new Vector3(0.15f, 0f, 0.15f), new Vector3(0.85f, 0.8f, 0.85f)) };

    static bool justLoaded;

    static BlockDatabase()
    {
        LoadAll();
        justLoaded = true;
    }

    static void LoadAll()
    {
        System.Array.Clear(infos, 0, infos.Length);
        System.Array.Clear(names, 0, names.Length);

        // Air : rien à dessiner, traversable, remplaçable (toujours présent, même sans fichier)
        infos[0] = new BlockInfo { replaceable = true, breakTime = -1f };
        names[0] = "Air";

        Load();
    }

    // Si le « Domain Reload » est désactivé dans les options du projet, les données de la partie précédente
    // sont encore là : on recharge les fichiers à chaque lancement, pour voir les blocs modifiés entre-temps.
    // (Avec le Domain Reload, la base vient d'être chargée à l'instant : rien à refaire.)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ReloadForNewPlaySession()
    {
        if (justLoaded) { justLoaded = false; return; }
        LoadAll();
    }

    // ------------------------------------------------------------------
    // Chargement des fichiers
    // ------------------------------------------------------------------

    static void Load()
    {
        BlockDefinition[] defs = Resources.LoadAll<BlockDefinition>(ResourcesFolder);
        if (defs == null || defs.Length == 0)
        {
            Debug.LogError($"BlockDatabase : aucun bloc trouvé dans Assets/Resources/{ResourcesFolder}/. " +
                           "Crée-les avec Create > Voxel > Bloc (ou Voxel > Exporter les blocs actuels).");
            defs = new BlockDefinition[0];
        }

        // 1) Numéros : entre 1 et 255, sans doublon
        var byId = new BlockDefinition[256];
        foreach (BlockDefinition def in defs)
        {
            if ((int)def.id <= 0 || (int)def.id > 255)
            {
                Debug.LogError($"BlockDatabase : le bloc « {def.name} » a le numéro {def.id} (il faut 1 à 255). Ignoré.", def);
                continue;
            }
            if (byId[(int)def.id] != null)
            {
                Debug.LogError($"BlockDatabase : « {def.name} » et « {byId[(int)def.id].name} » ont le même numéro {def.id}. " +
                               $"« {def.name} » est ignoré.", def);
                continue;
            }
            byId[(int)def.id] = def;
        }

        // Blocs intégrés (redstone) : ils servent tant qu'aucun fichier n'a le même numéro (un fichier les remplace)
        var taken = new HashSet<int>();
        foreach (BlockDefinition def in defs) taken.Add((int)def.id);
        BlockDefinition[] builtin = BuiltinBlocks.Create(taken);
        foreach (BlockDefinition def in builtin) byId[(int)def.id] = def;

        // Un bloc de l'enum BlockType sans fichier : le code l'utilise peut-être (génération...)
        foreach (BlockType t in System.Enum.GetValues(typeof(BlockType)))
            if (t != BlockType.Air && byId[(int)t] == null)
                Debug.LogWarning($"BlockDatabase : aucun fichier de bloc pour {t} (numéro {(int)t}). Il sera invisible et traversable.");

        // 2) Atlas : chaque image différente devient une tuile (la tuile 0 = « texture manquante »)
        var atlas = new AtlasBuilder();
        for (int id = 1; id < 256; id++)
            if (byId[id] != null) atlas.Collect(byId[id]);
        atlas.Build();
        Atlas = atlas.albedo;
        HeightAtlas = atlas.height;
        MetallicAtlas = atlas.metallic;
        EmissionAtlas = atlas.emission;
        AtlasTilesPerRow = atlas.tilesPerRow;
        translucentTiles = atlas.translucent ?? new bool[0];
        HasTranslucentTiles = false;
        foreach (bool tr in translucentTiles) HasTranslucentTiles |= tr;
        TilePixels = atlas.tilePixels;

        // 3) Infos de chaque bloc
        for (int id = 1; id < 256; id++)
        {
            BlockDefinition def = byId[id];
            if (def == null)
            {
                infos[id] = new BlockInfo { replaceable = true, breakTime = -1f }; // comme l'air
                continue;
            }
            infos[id] = ToInfo(def, atlas);
            for (int k = 0; k < 8; k++) { rotatedCollision[id * 8 + k] = null; rotatedSelection[id * 8 + k] = null; }
            names[id] = string.IsNullOrEmpty(def.displayName) ? def.name : def.displayName;
        }

        // Une variante murale lâche le bloc du sol (sauf si son fichier dit autre chose)
        for (int id = 1; id < 256; id++)
        {
            BlockType wall = infos[id].wallVariant;
            if (wall == BlockType.Air) continue;
            if (infos[(int)wall].dropOverride == BlockType.Air && infos[(int)wall].dropItem == ItemType.None)
                infos[(int)wall].dropOverride = (BlockType)id;
        }

        Debug.Log($"BlockDatabase : {defs.Length} bloc(s) chargé(s) + {builtin.Length} intégré(s) (redstone), atlas de {atlas.tilesPerRow} x {atlas.tilesPerRow} tuiles " +
                  $"de {atlas.tilePixels} pixels ({atlas.TileCount} tuile(s) utilisée(s)).");
    }

    static BlockInfo ToInfo(BlockDefinition d, AtlasBuilder atlas)
    {
        int side = atlas.TileOf(d.side.albedo);
        int top = d.top.albedo != null ? atlas.TileOf(d.top.albedo) : side;
        int bottom = d.bottom.albedo != null ? atlas.TileOf(d.bottom.albedo) : side;

        int[] faceTiles =
        {
            top, bottom,
            d.front != null && d.front.albedo != null ? atlas.TileOf(d.front.albedo) : side,
            d.back != null && d.back.albedo != null ? atlas.TileOf(d.back.albedo) : side,
            d.right != null && d.right.albedo != null ? atlas.TileOf(d.right.albedo) : side,
            d.left != null && d.left.albedo != null ? atlas.TileOf(d.left.albedo) : side,
        };

        var info = new BlockInfo
        {
            faceTiles = faceTiles,
            shape = d.shape,
            hasMesh = true,
            opaque = d.shape == BlockShape.Cube && d.opaque,  // seul un cube peut cacher ses voisins
            collidable = d.collidable && d.shape != BlockShape.Liquid,
            cullSameType = d.cullSameType,
            randomOffset = d.randomOffset,
            support = d.support,
            attachDir = d.attachDir,
            gravity = d.gravity,
            replaceable = d.replaceable,
            isSoil = d.isSoil,
            emission = (byte)Mathf.Clamp(d.emission, 0, 15),
            lightFilter = (byte)Mathf.Clamp(d.lightFilter, 0, 15),
            breakTime = d.breakTime,
            tool = d.tool,
            harvestLevel = d.harvestLevel,
            dropItem = d.dropItem,
            dropsNothing = d.dropsNothing,
            dropOverride = d.dropBlock != null ? (BlockType)d.dropBlock.id : BlockType.Air,
            dropCount = (byte)Mathf.Clamp(d.dropCount, 0, 64),
            dropCountRandom = (byte)Mathf.Clamp(d.dropCountRandom, 0, 64),
            lightMode = d.lightMode,
            wallVariant = d.wallVariant != null ? (BlockType)d.wallVariant.id : BlockType.Air,
            orientation = d.shape == BlockShape.Cube || d.shape == BlockShape.Model ? d.orientation : Orientation.None,
            tileTop = top, tileBottom = bottom, tileSide = side,
        };

        switch (d.shape)
        {
            case BlockShape.Cube:
                info.collisionBoxes = info.collidable ? FullBox : null;
                info.selectionBoxes = FullBox;
                break;

            case BlockShape.Cross:
                info.collidable = false;
                info.selectionBoxes = PlantBox;
                break;

            case BlockShape.Liquid:
                info.opaque = false;
                break;

            case BlockShape.Wire:
                // Fil plat : visé comme une plaque de 1/16, jamais solide
                info.opaque = false;
                info.collidable = false;
                info.selectionBoxes = new[] { new Box(Vector3.zero, new Vector3(1f, 0.0625f, 1f)) };
                break;

            case BlockShape.Repeater:
                // Dalle de 2/16 de haut : on peut marcher dessus
                info.opaque = false;
                info.collisionBoxes = info.collidable ? new[] { new Box(Vector3.zero, new Vector3(1f, 0.125f, 1f)) } : null;
                info.collidable = info.collisionBoxes != null;
                info.selectionBoxes = new[] { new Box(Vector3.zero, new Vector3(1f, 0.125f, 1f)) };
                break;

            case BlockShape.Model:
                if (d.blockbenchModel != null) ApplyBlockbench(ref info, d, atlas, top);
                else ApplyBoxes(ref info, d, atlas, top, bottom, side);
                break;
        }
        return info;
    }

    // Forme « Model » en boîtes (en 16èmes de bloc)
    static void ApplyBoxes(ref BlockInfo info, BlockDefinition d, AtlasBuilder atlas, int top, int bottom, int side)
    {
        ModelBox[] src = d.boxes ?? new ModelBox[0];
        var elements = new Element[src.Length];
        var boxes = new Box[src.Length];

        for (int i = 0; i < src.Length; i++)
        {
            ModelBox b = src[i];
            Vector3 min = Vector3.Min(b.from, b.to) / 16f, max = Vector3.Max(b.from, b.to) / 16f;
            int t = b.top != null ? atlas.TileOf(b.top) : top;
            int bo = b.bottom != null ? atlas.TileOf(b.bottom) : bottom;
            int s = b.side != null ? atlas.TileOf(b.side) : side;
            int[] ft = info.faceTiles;
            int fr = b.front != null ? atlas.TileOf(b.front) : (b.side == null && ft != null ? ft[2] : s);
            int ba = b.back != null ? atlas.TileOf(b.back) : (b.side == null && ft != null ? ft[3] : s);
            int ri = b.right != null ? atlas.TileOf(b.right) : (b.side == null && ft != null ? ft[4] : s);
            int le = b.left != null ? atlas.TileOf(b.left) : (b.side == null && ft != null ? ft[5] : s);
            elements[i] = new Element(min, max, new[] { t, bo, fr, ba, ri, le });
            boxes[i] = new Box(min, max);
        }

        info.elements = elements;
        info.flushMask = FlushMask(elements);
        info.collisionBoxes = info.collidable && boxes.Length > 0 ? boxes : null;
        info.collidable = info.collisionBoxes != null;
        info.selectionBoxes = boxes.Length > 0 ? boxes : FullBox;
    }

    // Forme « Model » importée de Blockbench : les noms de textures du .json sont reliés aux images du fichier de bloc
    static void ApplyBlockbench(ref BlockInfo info, BlockDefinition d, AtlasBuilder atlas, int defaultTile)
    {
        var byName = new Dictionary<string, int>();
        foreach (NamedTexture nt in d.blockbenchTextures ?? new NamedTexture[0])
            if (nt != null && !string.IsNullOrEmpty(nt.name) && nt.texture != null)
                byName[Simplify(nt.name)] = atlas.TileOf(nt.texture);

        try
        {
            BlockbenchModel.Bake(d.blockbenchModel.text,
                                 name => byName.TryGetValue(Simplify(name), out int tile) ? tile : -1,
                                 defaultTile, out ModelQuad[] quads, out Box[] boxes);

            Box[] collision;
            switch (d.blockbenchCollision)
            {
                case ModelCollision.None:      collision = null; break;
                case ModelCollision.FullBlock: collision = FullBox; break;
                default:                       collision = boxes.Length > 0 ? boxes : null; break;
            }

            info.quads = quads;
            info.collisionBoxes = d.collidable ? collision : null;
            info.collidable = info.collisionBoxes != null;
            info.selectionBoxes = boxes.Length > 0 ? boxes : FullBox;
        }
        catch (System.Exception e)
        {
            // Un modèle cassé ne doit pas empêcher le jeu de démarrer : on garde un cube bien visible
            Debug.LogError($"BlockDatabase : modèle Blockbench de « {d.name} » impossible à charger ({e.Message}). Remplacé par un cube.", d);
            info.shape = BlockShape.Cube;
            info.collisionBoxes = FullBox;
            info.selectionBoxes = FullBox;
            info.collidable = true;
        }
    }

    // « minecraft:block/anvil » -> « anvil »
    static string Simplify(string name)
    {
        int i = name.LastIndexOf('/');
        if (i >= 0) name = name.Substring(i + 1);
        i = name.LastIndexOf(':');
        if (i >= 0) name = name.Substring(i + 1);
        return name.ToLowerInvariant();
    }

    // Quelles faces du modèle sont posées exactement contre le bord de la case ?
    // (une dalle du bas : le bas et les 4 côtés, mais PAS le haut, qui est à mi-hauteur)
    static byte FlushMask(Element[] elements)
    {
        const float e = 1e-4f;
        int mask = 0;

        foreach (Element el in elements)
        {
            if (el.max.y >= 1f - e) mask |= 1 << 0;   // haut
            if (el.min.y <= e)      mask |= 1 << 1;   // bas
            if (el.max.z >= 1f - e) mask |= 1 << 2;   // +Z
            if (el.min.z <= e)      mask |= 1 << 3;   // -Z
            if (el.max.x >= 1f - e) mask |= 1 << 4;   // +X
            if (el.min.x <= e)      mask |= 1 << 5;   // -X
        }

        return (byte)mask;
    }

    // ------------------------------------------------------------------
    // Accès
    // ------------------------------------------------------------------

    public static BlockInfo Get(BlockType type) => infos[(int)type];

    // Boîtes de collision / de visée d'un bloc selon son orientation (état). Mises en cache.
    static readonly Box[][] rotatedCollision = new Box[256 * 8][];
    static readonly Box[][] rotatedSelection = new Box[256 * 8][];

    public static Box[] CollisionBoxes(BlockType type, byte state)
    {
        ref readonly BlockInfo info = ref infos[(int)type];
        if (info.orientation == Orientation.None || state == 0 || info.collisionBoxes == null) return info.collisionBoxes;

        int k = (int)type * 8 + (state & 7);
        return rotatedCollision[k] ?? (rotatedCollision[k] = BlockOrientation.RotateBoxes(info.collisionBoxes, info.orientation, BlockOrientation.Clamp(info.orientation, state)));
    }

    public static Box[] SelectionBoxes(BlockType type, byte state)
    {
        ref readonly BlockInfo info = ref infos[(int)type];
        if (info.orientation == Orientation.None || state == 0 || info.selectionBoxes == null) return info.selectionBoxes;

        int k = (int)type * 8 + (state & 7);
        return rotatedSelection[k] ?? (rotatedSelection[k] = BlockOrientation.RotateBoxes(info.selectionBoxes, info.orientation, BlockOrientation.Clamp(info.orientation, state)));
    }

    // Comme Get, mais SANS copier la structure (à privilégier dans les boucles chaudes)
    public static ref readonly BlockInfo GetRef(BlockType type) => ref infos[(int)type];

    // Nom affiché (champ « Display Name » du fichier, sinon le nom du fichier)
    public static string Name(BlockType type) => names[(int)type] ?? type.ToString();

    public static bool HasMesh(BlockType type) => infos[(int)type].hasMesh;
    public static bool IsOpaque(BlockType type) => infos[(int)type].opaque;

    // Lumière absorbée par un bloc : 0 = transparent, 15 = opaque (bloque tout)
    public static int LightOpacity(BlockType type) => infos[(int)type].opaque ? 15 : infos[(int)type].lightFilter;

    // Lumière émise par un bloc (0 à 15)
    public static int Emission(BlockType type) => infos[(int)type].emission;

    // Hauteur de la surface sur laquelle un bloc qui tombe se pose, dans la case : 1 pour un cube,
    // 0,5 pour une dalle... 0 = aucune collision (herbe, torche, feuilles) : il est traversé.
    public static float SupportHeight(BlockType type)
    {
        Box[] boxes = infos[(int)type].collisionBoxes;
        if (boxes == null) return 0f;

        float top = 0f;
        for (int i = 0; i < boxes.Length; i++) top = Mathf.Max(top, boxes[i].max.y);
        return top;
    }

    // face : 0 = haut, 1 = bas, 2..5 = côtés (cubes uniquement)
    public static int GetTile(BlockType type, int face)
    {
        ref readonly BlockInfo info = ref infos[(int)type];
        if (info.faceTiles != null) return info.faceTiles[face];
        if (face == 0) return info.tileTop;
        if (face == 1) return info.tileBottom;
        return info.tileSide;
    }

    // Faut-il dessiner la face d'un cube `current` qui regarde `neighbor` ?
    public static bool ShouldDrawFace(BlockType current, BlockType neighbor)
    {
        if (infos[(int)neighbor].opaque) return false;
        if (neighbor == current && infos[(int)current].cullSameType) return false;
        return true;
    }

    // Un cube visible (même sans collision : feuilles, vitre) cache la face de son voisin dans le mesh de raycast
    public static bool IsFullCube(BlockType type)
    {
        return infos[(int)type].hasMesh && infos[(int)type].shape == BlockShape.Cube;
    }

    // ------------------------------------------------------------------
    // UVs
    // ------------------------------------------------------------------

    // UV d'un point de la face, calculées à partir de sa POSITION dans le bloc (0..1).
    // Un cube entier reprend toute la tuile ; un cuboïde partiel prend la portion correspondante,
    // exactement alignée sur la grille de pixels (comme l'UV par défaut de Minecraft).
    public static Vector2 FaceUV(int tile, int face, Vector3 p)
    {
        float u, v;
        switch (face)
        {
            case 0:  u = p.x;        v = p.z; break; // haut
            case 1:  u = p.z;        v = p.x; break; // bas
            case 2:  u = 1f - p.x;   v = p.y; break; // +Z
            case 3:  u = p.x;        v = p.y; break; // -Z
            case 4:  u = p.z;        v = p.y; break; // +X
            default: u = 1f - p.z;   v = p.y; break; // -X
        }
        return TileUV(tile, u, v);
    }

    // Point (u, v) de 0 à 1 à l'intérieur d'une tuile, converti en UV d'atlas
    public static Vector2 TileUV(int tile, float u, float v)
    {
        int perRow = AtlasTilesPerRow;
        float size = 1f / perRow;
        int col = tile % perRow;
        int row = tile / perRow;

        // Marge minuscule contre les coutures (voir UvInsetTexels)
        float pad = UvInsetTexels / (perRow * TilePixels);

        float u0 = col * size + pad;
        float u1 = (col + 1) * size - pad;
        // En Unity, v = 0 est en BAS de la texture : la rangée 0 est en haut
        float v1 = 1f - row * size - pad;
        float v0 = 1f - (row + 1) * size + pad;

        return new Vector2(Mathf.Lerp(u0, u1, u), Mathf.Lerp(v0, v1, v));
    }

    // ------------------------------------------------------------------
    // Assemblage de l'atlas
    // ------------------------------------------------------------------

    // Chaque image différente devient une tuile. Les cartes de relief, de métal et d'émission d'une image
    // vont dans la même tuile de leurs propres atlas (même disposition).
    sealed class AtlasBuilder
    {
        readonly List<Texture2D> albedos = new List<Texture2D> { null }; // tuile 0 : « texture manquante »
        readonly Dictionary<Texture2D, int> tiles = new Dictionary<Texture2D, int>();
        readonly List<FaceTextures> extras = new List<FaceTextures> { null };

        public Texture2D albedo, height, metallic, emission;
        public int tilesPerRow = 1, tilePixels = 16;
        public int TileCount => albedos.Count;
        public bool[] translucent;

        public void Collect(BlockDefinition d)
        {
            Add(d.side); Add(d.top); Add(d.bottom); Add(d.front); Add(d.back); Add(d.right); Add(d.left);
            foreach (ModelBox b in d.boxes ?? new ModelBox[0])
            {
                if (b == null) continue;
                Add(b.top); Add(b.side); Add(b.bottom); Add(b.front); Add(b.back); Add(b.right); Add(b.left);
            }
            foreach (NamedTexture nt in d.blockbenchTextures ?? new NamedTexture[0])
                if (nt != null) Add(nt.texture);
        }

        void Add(FaceTextures f)
        {
            if (f == null || f.albedo == null) return;
            int tile = Add(f.albedo);
            // Les cartes en plus : la première déclarée pour cette image l'emporte
            if (extras[tile] == null && (f.height != null || f.metallic != null || f.emission != null))
                extras[tile] = f;
        }

        int Add(Texture2D t)
        {
            if (t == null) return 0;
            if (tiles.TryGetValue(t, out int tile)) return tile;
            tile = albedos.Count;
            albedos.Add(t);
            extras.Add(null);
            tiles[t] = tile;
            return tile;
        }

        public int TileOf(Texture2D t) => t != null && tiles.TryGetValue(t, out int tile) ? tile : 0;

        public void Build()
        {
            // Taille d'une tuile : celle de la plupart des images (les autres sont redimensionnées, avec un avertissement)
            var counts = new Dictionary<int, int>();
            for (int i = 1; i < albedos.Count; i++)
            {
                int w = albedos[i].width;
                counts[w] = counts.TryGetValue(w, out int c) ? c + 1 : 1;
            }
            int best = 16, bestCount = -1;
            foreach (var kv in counts)
                if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
            tilePixels = best;
            tilesPerRow = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(albedos.Count)));

            for (int i = 1; i < albedos.Count; i++)
                if (albedos[i].width != tilePixels || albedos[i].height != tilePixels)
                    Debug.LogWarning($"BlockDatabase : l'image « {albedos[i].name} » fait {albedos[i].width} x {albedos[i].height} pixels, " +
                                     $"les autres {tilePixels} x {tilePixels} : elle est redimensionnée.", albedos[i]);

            albedo = Pack(i => albedos[i], new Color32(0, 0, 0, 255), false, "Atlas des blocs", missing: true);

            bool anyHeight = false, anyMetal = false, anyEmission = false;
            foreach (FaceTextures f in extras)
            {
                if (f == null) continue;
                anyHeight |= f.height != null; anyMetal |= f.metallic != null; anyEmission |= f.emission != null;
            }
            if (anyHeight) height = Pack(i => extras[i]?.height, new Color32(255, 255, 255, 255), true, "Atlas de relief");
            if (anyMetal) metallic = Pack(i => extras[i]?.metallic, new Color32(0, 0, 0, 0), true, "Atlas de métal");
            if (anyEmission) emission = Pack(i => extras[i]?.emission, new Color32(0, 0, 0, 255), false, "Atlas d'émission");
        }

        Texture2D Pack(System.Func<int, Texture2D> source, Color32 fill, bool linear, string name, bool missing = false)
        {
            int size = tilesPerRow * tilePixels;
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;

            for (int tile = 0; tile < albedos.Count; tile++)
            {
                Color32[] src = tile == 0 && missing ? MissingTile(tilePixels) : Read(source(tile), tilePixels, linear);
                if (src == null) continue;

                if (missing)
                {
                    if (translucent == null || translucent.Length != albedos.Count) translucent = new bool[albedos.Count];
                    for (int k = 0; k < src.Length; k++)
                        if (src[k].a > 6 && src[k].a < 250) { translucent[tile] = true; break; }
                }

                int col = tile % tilesPerRow, row = tile / tilesPerRow;
                int ox = col * tilePixels;
                int oy = (tilesPerRow - 1 - row) * tilePixels; // rangée 0 en HAUT de l'image (comme TileUV)
                for (int y = 0; y < tilePixels; y++)
                    System.Array.Copy(src, y * tilePixels, pixels, (oy + y) * size + ox, tilePixels);
            }

            var atlas = new Texture2D(size, size, TextureFormat.RGBA32, false, linear)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            atlas.SetPixels32(pixels);
            atlas.Apply(false, false); // reste lisible (icônes de l'inventaire)
            return atlas;
        }

        // Damier magenta et noir : une face sans texture se voit tout de suite
        static Color32[] MissingTile(int n)
        {
            var p = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                p[y * n + x] = ((x * 2 / n) + (y * 2 / n)) % 2 == 0 ? new Color32(255, 0, 255, 255) : new Color32(0, 0, 0, 255);
            return p;
        }

        // Pixels d'une image, à la taille d'une tuile. Marche même si l'image n'est pas « Read/Write » :
        // elle est alors recopiée dans une RenderTexture que l'on relit.
        static Color32[] Read(Texture2D t, int n, bool linear)
        {
            if (t == null) return null;

            Color32[] src;
            int w = t.width, h = t.height;
            if (t.isReadable)
            {
                src = t.GetPixels32();
            }
            else
            {
                RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                                                              linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                Graphics.Blit(t, rt);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                src = copy.GetPixels32();
                Object.Destroy(copy);
            }

            if (w == n && h == n) return src;

            // Redimensionnement au plus proche (garde des pixels nets)
            var dst = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                dst[y * n + x] = src[(y * h / n) * w + (x * w / n)];
            return dst;
        }
    }
}
