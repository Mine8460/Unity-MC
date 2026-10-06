using System.Collections.Generic;
using UnityEngine;

// ATTENTION : ne change jamais les valeurs existantes (elles sont écrites dans les sauvegardes).
// Ajoute toujours les nouveaux blocs à la fin.
public enum BlockType : byte
{
    Air = 0,
    Bedrock,   // bloc indestructible (ne peut pas être placé)
    Grass,
    Dirt,
    Stone,
    Log,
    Leaves,
    Glass,
    TallGrass,
    StoneSlab,
    Anvil,
    AnvilRotated,
    Torch,
    Sand,
    StoneSlabTop,
    StoneDoubleSlab,
    Water,     // NOUVEAU : toujours à la fin, pour ne pas décaler les blocs des sauvegardes
    CoalOre,
    IronOre,
    GoldOre,
    DiamondOre,
    Planks,
    CraftingTable,
    WallTorchNorth,
    WallTorchEast,
    WallTorchSouth,
    WallTorchWest,
}

public enum BlockShape : byte
{
    Cube,   // cube plein (feuilles et vitre en font partie)
    Cross,  // deux plans en X, double face (herbes hautes, fleurs...)
    Model,  // assemblage de cuboïdes (dalle, enclume, torche...)
    Liquid, // eau : transparente, sans collision, dessinée avec son propre matériau
}

// Comment le shader éclaire un bloc (la valeur est écrite dans l'alpha des couleurs de sommets)
public enum LightMode : byte
{
    Pixel = 0,         // pixels d'ombre alignés sur la texture (cubes, modèles)
    Block = 128,       // une seule ombre par bloc, lue au-dessus du bloc (plantes en diagonale)
    FullBright = 255,  // ni lumière ni ombre : couleurs exactes de la texture (torche)
}

// Boîte alignée sur les axes, en coordonnées de bloc (0..1)
public readonly struct Box
{
    public readonly Vector3 min, max;
    public Box(Vector3 min, Vector3 max) { this.min = min; this.max = max; }
}

// Un cuboïde d'un modèle. Coordonnées en blocs (0..1).
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
    None,        // aucun
    SolidBelow,  // un cube plein et solide sous lui (torche)
    SoilBelow,   // de l'herbe ou de la terre sous lui (plantes)
    SolidBehind,
}

public struct BlockInfo
{
    public BlockShape shape;
    public bool hasMesh;        // false pour l'air : rien à dessiner
    public bool opaque;         // true = cache les faces des blocs voisins
    public bool collidable;
    public bool cullSameType;   // pas de face entre deux blocs identiques (vitre, dalle...)
    public bool randomOffset;   // décalage aléatoire par position (herbes hautes)
    public SupportRule support; // ce dont le bloc a besoin sous lui pour exister
    public Vector3Int attachDir;
    public bool gravity;        // tombe quand il n'a plus rien dessous (enclume)
    public bool replaceable;    // peut être remplacé par un autre bloc (air, herbe, torche)
    public bool isSoil;         // les plantes peuvent y pousser (herbe, terre)
    public byte emission;       // lumière émise, de 0 à 15 (torche : 14)
    public byte lightFilter;    // lumière absorbée en traversant le bloc (feuilles : 1). Un bloc opaque bloque tout.
    public float breakTime;     // temps de casse à la main, en secondes (0 = instantané, négatif = incassable)
    public ToolKind tool;       // outil qui accélère la casse (pioche, hache, pelle)
    public int harvestLevel;    // niveau d'outil exigé pour que le bloc lâche quelque chose (0 = aucun ; 1 bois, 2 pierre, 3 fer, 4 diamant)
    public ItemType dropItem;   // objet lâché à la place du bloc (minerai de charbon -> charbon) ; None = le bloc
    public bool dropsNothing;       // ne lâche rien quand il est cassé (feuilles, vitre, herbe haute)
    public BlockType dropOverride;  // lâche un autre objet (l'herbe lâche de la terre) ; Air = lâche le bloc lui-même
    public byte dropCount;          // nombre d'objets lâchés (0 = un seul)
    public byte flushMask;          // faces (bit 0 = haut, 1 = bas, 2 = +Z, 3 = -Z, 4 = +X, 5 = -X) posées contre le bord du bloc
    public LightMode lightMode; // éclairage par le shader (Pixel par défaut)
    public int tileTop, tileBottom, tileSide;   // cubes et plantes
    public Element[] elements;                  // formes "Model"
    public ModelQuad[] quads;                   // modèles importés (Blockbench)
    public Box[] collisionBoxes;                // null = aucune collision
    public Box[] selectionBoxes;                // zone visée par le raycast (casser/poser), même sans collision
}

public static class BlockDatabase
{
    // Atlas carré de N x N tuiles (ici 4x4 = 16 tuiles)
    public const int AtlasTilesPerRow = 8;

    // Tuile de l'eau dans l'atlas (une tuile libre, peinte en bleu ; la transparence vient du matériau)
    public const int WaterTile = 13;

    // Résolution d'UNE tuile en pixels (doit correspondre à "Pixels par bloc" du shader)
    public const int TilePixels = 16;

    // Marge UV en fraction de pixel de texture. DOIT être identique à "uvInset" dans PixelShadowLit.shader
    public const float UvInsetTexels = 0.01f;

    // Index des tuiles (0 = coin haut gauche, lecture gauche→droite, haut→bas) :
    //  0 herbe dessus | 1 herbe côté  | 2 terre       | 3 pierre
    //  4 tronc côté   | 5 tronc dessus| 6 feuilles    | 7 vitre
    //  8 herbe haute  | 9 enclume (dessus) | 10 enclume (métal) | 11 torche

    static readonly BlockInfo[] infos =
        new BlockInfo[System.Enum.GetValues(typeof(BlockType)).Length];

    static readonly Box[] FullBox = { new Box(Vector3.zero, Vector3.one) };

    // Zone visée pour une plante en croix (un peu plus étroite que le bloc)
    static readonly Box[] PlantBox = { new Box(new Vector3(0.1f, 0f, 0.1f), new Vector3(0.9f, 1f, 0.9f)) };

    // Noms de tuiles : sert à retrouver la bonne tuile d'après le nom de la texture d'un modèle Blockbench
    // ("block/anvil" -> "anvil"). Une texture inconnue utilise la tuile par défaut donnée à FromBlockbench.
    static readonly Dictionary<string, int> tileNames = new()
    {
        { "grass_top", 0 }, { "grass_side", 1 }, { "dirt", 2 },       { "stone", 3 },
        { "log_side", 4 },  { "log_top", 5 },    { "leaves", 6 },     { "glass", 7 },
        { "tall_grass", 8 },{ "anvil_top", 9 },  { "anvil", 10 },     { "torch", 11 },
    };

    public static int TileByName(string name)
    {
        return tileNames.TryGetValue(name.ToLowerInvariant(), out int index) ? index : -1;
    }

    // Une torche murale : même lumière que la torche, accrochée à son mur, et elle lâche une torche normale
    static void WallTorch(BlockType type, Element stick, Vector3Int wallDirection)
    {
        BlockInfo info = WithLight(Model(new[] { stick }, collidable: false, cullSameType: false), LightMode.FullBright);
        info.emission = 14;
        info.support = SupportRule.SolidBehind;
        info.attachDir = wallDirection;
        info.dropOverride = BlockType.Torch;
        info.breakTime = 0f;   // instantanée, comme la torche
        infos[(int)type] = info;
    }

    static BlockDatabase()
    {
        // AJOUTER UN BLOC = une entrée dans l'enum + une ligne ici (+ sa tuile dans l'atlas)
        infos[(int)BlockType.Air]   = new BlockInfo { hasMesh = false };
        infos[(int)BlockType.Grass] = Solid(top: 0, bottom: 2, side: 1);
        infos[(int)BlockType.Dirt]  = Solid(top: 2, bottom: 2, side: 2);
        infos[(int)BlockType.Stone] = Solid(top: 3, bottom: 3, side: 3);
        infos[(int)BlockType.Log]   = Solid(top: 7, bottom: 7, side: 6);
        infos[(int)BlockType.Bedrock] = Solid(top: 4, bottom: 4, side: 4);

        // Feuilles : visibles, transparentes, sans collision
        infos[(int)BlockType.Leaves] = new BlockInfo
        {
            shape = BlockShape.Cube, hasMesh = true, opaque = false, collidable = false,
            tileTop = 5, tileBottom = 5, tileSide = 5
        };

        // Vitre : transparente mais avec collision, et sans faces internes
        infos[(int)BlockType.Glass] = new BlockInfo
        {
            shape = BlockShape.Cube, hasMesh = true, opaque = false, collidable = true,
            cullSameType = true, collisionBoxes = FullBox,
            tileTop = 8, tileBottom = 8, tileSide = 8
        };

        // Herbe haute : deux plans en X, sans collision, léger décalage aléatoire
        infos[(int)BlockType.TallGrass] = Plant(tile: 9, randomOffset: true);

        // Dalle de pierre (moitié basse du bloc)
        infos[(int)BlockType.StoneSlab] = Model(
            new[] { E(0, 0, 0, 16, 8, 16, top: 3, bottom: 3, side: 3) },
            collidable: true, cullSameType: true);

        // Dalle de pierre en haut du bloc, et bloc plein formé de deux dalles
        infos[(int)BlockType.StoneSlabTop] = Model(
            new[] { E(0, 8, 0, 16, 16, 16, top: 3, bottom: 3, side: 3) },
            collidable: true, cullSameType: true);
        infos[(int)BlockType.StoneDoubleSlab] = Solid(top: 3, bottom: 3, side: 3);

        // Enclume (approximation du modèle vanilla). AnvilRotated = même modèle tourné de 90°.
        infos[(int)BlockType.Anvil] = Model(AnvilElements(), collidable: true, cullSameType: false);
        infos[(int)BlockType.AnvilRotated] = Model(RotateY(AnvilElements()), collidable: true, cullSameType: false);

        // Torche : fine colonne, sans collision
        infos[(int)BlockType.Torch] = WithLight(Model(
            new[] { E(7, 0, 7, 9, 10, 9, top: 11, bottom: 11, side: 11) },
            collidable: false, cullSameType: false), LightMode.FullBright);

        WallTorch(BlockType.WallTorchNorth, E(7, 3, 14, 9, 13, 16, top: 11, bottom: 11, side: 11), new Vector3Int(0, 0, 1));
        WallTorch(BlockType.WallTorchSouth, E(7, 3, 0, 9, 13, 2, top: 11, bottom: 11, side: 11), new Vector3Int(0, 0, -1));
        WallTorch(BlockType.WallTorchEast, E(14, 3, 7, 16, 13, 9, top: 11, bottom: 11, side: 11), new Vector3Int(1, 0, 0));
        WallTorch(BlockType.WallTorchWest, E(0, 3, 7, 2, 13, 9, top: 11, bottom: 11, side: 11), new Vector3Int(-1, 0, 0));

        // (tuiles de test : adapte-les à ton atlas)
        infos[(int)BlockType.Sand]    = Solid(top: 12, bottom: 12, side: 12);

        // Minerais (tuiles de test : mets celles de ton atlas)
        infos[(int)BlockType.CoalOre]    = Solid(top: 16, bottom: 16, side: 16);
        infos[(int)BlockType.IronOre]    = Solid(top: 17, bottom: 17, side: 17);
        infos[(int)BlockType.GoldOre]    = Solid(top: 18, bottom: 18, side: 18);
        infos[(int)BlockType.DiamondOre] = Solid(top: 19, bottom: 19, side: 19);

        // Planches et établi (tuiles de test : mets celles de ton atlas)
        infos[(int)BlockType.Planks]        = Solid(top: 14, bottom: 14, side: 14);
        infos[(int)BlockType.CraftingTable] = Solid(top: 15, bottom: 14, side: 16);

        // Temps de casse à la main, en secondes (0 = instantané, -1 = incassable)
        SetBreakTime(BlockType.Bedrock, -1f);
        SetBreakTime(BlockType.Grass, 0.9f);
        SetBreakTime(BlockType.Dirt, 0.75f);
        SetBreakTime(BlockType.Sand, 0.75f);
        SetBreakTime(BlockType.Stone, 2f);
        SetBreakTime(BlockType.Log, 3f);
        SetBreakTime(BlockType.Leaves, 0.35f);
        SetBreakTime(BlockType.Glass, 0.45f);
        SetBreakTime(BlockType.TallGrass, 0f);
        SetBreakTime(BlockType.Torch, 0f);
        SetBreakTime(BlockType.WallTorchNorth, 0f);
        SetBreakTime(BlockType.WallTorchSouth, 0f);
        SetBreakTime(BlockType.WallTorchEast, 0f);
        SetBreakTime(BlockType.WallTorchWest, 0f);
        SetBreakTime(BlockType.StoneSlab, 2f);
        SetBreakTime(BlockType.StoneSlabTop, 2f);
        SetBreakTime(BlockType.StoneDoubleSlab, 2f);
        SetBreakTime(BlockType.Anvil, 4f);
        SetBreakTime(BlockType.AnvilRotated, 4f);
        SetBreakTime(BlockType.CoalOre, 3f);
        SetBreakTime(BlockType.IronOre, 3.5f);
        SetBreakTime(BlockType.GoldOre, 3.5f);
        SetBreakTime(BlockType.DiamondOre, 4f);
        SetBreakTime(BlockType.Planks, 3f);
        SetBreakTime(BlockType.CraftingTable, 3.75f);

        // Outils : lequel accélère la casse, et quel niveau il faut pour récupérer le bloc
        SetTool(BlockType.Grass, ToolKind.Shovel);
        SetTool(BlockType.Dirt, ToolKind.Shovel);
        SetTool(BlockType.Sand, ToolKind.Shovel);
        SetTool(BlockType.Log, ToolKind.Axe);
        SetTool(BlockType.Planks, ToolKind.Axe);
        SetTool(BlockType.CraftingTable, ToolKind.Axe);
        SetTool(BlockType.Stone, ToolKind.Pickaxe, 1);
        SetTool(BlockType.StoneSlab, ToolKind.Pickaxe, 1);
        SetTool(BlockType.StoneSlabTop, ToolKind.Pickaxe, 1);
        SetTool(BlockType.StoneDoubleSlab, ToolKind.Pickaxe, 1);
        SetTool(BlockType.Anvil, ToolKind.Pickaxe, 1);
        SetTool(BlockType.AnvilRotated, ToolKind.Pickaxe, 1);
        SetTool(BlockType.CoalOre, ToolKind.Pickaxe, 1);
        SetTool(BlockType.IronOre, ToolKind.Pickaxe, 2);
        SetTool(BlockType.GoldOre, ToolKind.Pickaxe, 3);
        SetTool(BlockType.DiamondOre, ToolKind.Pickaxe, 3);

        // Minerais qui lâchent un objet (le fer et l'or lâchent leur minerai : il faudra le cuire au fourneau)
        infos[(int)BlockType.CoalOre].dropItem = ItemType.Coal;
        infos[(int)BlockType.DiamondOre].dropItem = ItemType.Diamond;

        // Eau : transparente, sans collision, on peut poser un bloc dedans. Elle assombrit la lumière
        // qui la traverse (1 niveau par bloc) : le fond des lacs profonds est plus sombre.
        infos[(int)BlockType.Water] = new BlockInfo
        {
            shape = BlockShape.Liquid, hasMesh = true, opaque = false, collidable = false,
            replaceable = true, dropsNothing = true, lightFilter = 1,
            tileTop = WaterTile, tileBottom = WaterTile, tileSide = WaterTile
        };

        // ------------------------------------------------------------------
        // Règles des blocs : une ligne par règle, pour chaque bloc concerné
        // ------------------------------------------------------------------
        infos[(int)BlockType.Air].replaceable = true;

        infos[(int)BlockType.Grass].isSoil = true;
        infos[(int)BlockType.Dirt].isSoil = true;

        infos[(int)BlockType.TallGrass].support = SupportRule.SoilBelow;   // pousse sur l'herbe ou la terre
        infos[(int)BlockType.TallGrass].replaceable = true;

        infos[(int)BlockType.Torch].support = SupportRule.SolidBelow;      // posée sur un cube plein
        infos[(int)BlockType.WallTorchNorth].support = SupportRule.SolidBehind;
        infos[(int)BlockType.WallTorchSouth].support = SupportRule.SolidBehind;
        infos[(int)BlockType.WallTorchEast].support = SupportRule.SolidBehind;
        infos[(int)BlockType.WallTorchWest].support = SupportRule.SolidBehind;
        // La torche n'est PAS remplaçable : un bloc qui tombe dessus ne peut pas se poser, il devient un objet

        infos[(int)BlockType.Anvil].gravity = true;
        infos[(int)BlockType.AnvilRotated].gravity = true;

        infos[(int)BlockType.Torch].emission = 14;        // la torche éclaire (0 à 15)
        infos[(int)BlockType.WallTorchNorth].emission = 14;        // la torche éclaire (0 à 15)
        infos[(int)BlockType.WallTorchSouth].emission = 14;        // la torche éclaire (0 à 15)
        infos[(int)BlockType.WallTorchEast].emission = 14;        // la torche éclaire (0 à 15)
        infos[(int)BlockType.WallTorchWest].emission = 14;        // la torche éclaire (0 à 15)
        infos[(int)BlockType.Leaves].lightFilter = 1;     // les feuilles assombrissent la lumière qui les traverse

        // Objets lâchés quand le bloc est cassé
        infos[(int)BlockType.Grass].dropOverride = BlockType.Dirt;   // l'herbe lâche de la terre
        infos[(int)BlockType.Leaves].dropsNothing = true;
        infos[(int)BlockType.Glass].dropsNothing = true;
        infos[(int)BlockType.TallGrass].dropsNothing = true;

        // Les dalles lâchent toujours la dalle (la seule qu'on peut avoir dans l'inventaire) : deux pour un bloc plein
        infos[(int)BlockType.StoneSlabTop].dropOverride = BlockType.StoneSlab;
        infos[(int)BlockType.StoneDoubleSlab].dropOverride = BlockType.StoneSlab;
        infos[(int)BlockType.StoneDoubleSlab].dropCount = 2;
    }

    // ------------------------------------------------------------------
    // Modèles
    // ------------------------------------------------------------------

    static Element[] AnvilElements() => new[]
    {
        E(2, 0, 2, 14, 4, 14,  top: 10, bottom: 10, side: 10),   // pied
        E(4, 4, 3, 12, 5, 13,  top: 10, bottom: 10, side: 10),   // plaque
        E(6, 5, 4, 10, 10, 12, top: 10, bottom: 10, side: 10),   // tige
        E(0, 10, 3, 16, 16, 13, top: 10, bottom: 10, side: 10),   // dessus
    };

    // Cuboïde en 16èmes de bloc, comme dans Minecraft
    static Element E(float x0, float y0, float z0, float x1, float y1, float z1, int top, int bottom, int side)
    {
        return new Element(
            new Vector3(x0, y0, z0) / 16f,
            new Vector3(x1, y1, z1) / 16f,
            new[] { top, bottom, side, side, side, side });
    }

    // Tourne un modèle de 90° autour de l'axe Y (position ET tuiles des faces)
    static Element[] RotateY(Element[] src)
    {
        var dst = new Element[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            Element e = src[i];

            // (x, z) -> (z, 1 - x)
            Vector3 min = new Vector3(e.min.z, e.min.y, 1f - e.max.x);
            Vector3 max = new Vector3(e.max.z, e.max.y, 1f - e.min.x);

            // Faces : haut, bas, +Z, -Z, +X, -X  ->  la face +X devient -Z, +Z devient +X, etc.
            int[] t = e.tiles;
            dst[i] = new Element(min, max, new[] { t[0], t[1], t[5], t[4], t[2], t[3] });
        }
        return dst;
    }

    // Charge un modèle exporté de Blockbench (Java Block/Item) depuis Assets/Resources/<resourcePath>.json
    //   defaultTile : tuile de l'atlas utilisée pour les textures que le nom ne permet pas de reconnaître
    //   collision   : none / une boîte par élément / cube plein
    static BlockInfo FromBlockbench(string resourcePath, int defaultTile,
                                    ModelCollision collision = ModelCollision.Elements,
                                    bool cullSameType = false)
    {
        try
        {
            TextAsset asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
                throw new System.IO.FileNotFoundException($"Assets/Resources/{resourcePath}.json introuvable");

            BlockbenchModel.Bake(asset.text, TileByName, defaultTile, out ModelQuad[] quads, out Box[] boxes);

            Box[] collisionBoxes;
            switch (collision)
            {
                case ModelCollision.None:      collisionBoxes = null; break;
                case ModelCollision.FullBlock: collisionBoxes = FullBox; break;
                default:                       collisionBoxes = boxes.Length > 0 ? boxes : null; break;
            }

            return new BlockInfo
            {
                shape = BlockShape.Model, hasMesh = true, opaque = false,
                collidable = collisionBoxes != null, cullSameType = cullSameType,
                quads = quads, collisionBoxes = collisionBoxes,
                selectionBoxes = boxes.Length > 0 ? boxes : FullBox
            };
        }
        catch (System.Exception e)
        {
            // Un modèle cassé ne doit pas empêcher le jeu de démarrer : on met un cube bien visible à la place
            Debug.LogError($"BlockDatabase : modèle \"{resourcePath}\" impossible à charger ({e.Message}). Remplacé par un cube.");
            return Solid(defaultTile, defaultTile, defaultTile);
        }
    }

    // ------------------------------------------------------------------
    // Constructeurs de BlockInfo
    // ------------------------------------------------------------------

    static void SetBreakTime(BlockType type, float seconds) => infos[(int)type].breakTime = seconds;

    static void SetTool(BlockType type, ToolKind tool, int harvestLevel = 0)
    {
        infos[(int)type].tool = tool;
        infos[(int)type].harvestLevel = harvestLevel;
    }

    static BlockInfo Solid(int top, int bottom, int side) => new BlockInfo
    {
        shape = BlockShape.Cube, hasMesh = true, opaque = true, collidable = true,
        collisionBoxes = FullBox,
        tileTop = top, tileBottom = bottom, tileSide = side
    };

    // Copie d'un BlockInfo avec un autre mode d'éclairage
    static BlockInfo WithLight(BlockInfo info, LightMode mode)
    {
        info.lightMode = mode;
        return info;
    }

    static BlockInfo Plant(int tile, bool randomOffset) => new BlockInfo
    {
        shape = BlockShape.Cross, hasMesh = true, opaque = false, collidable = false,
        randomOffset = randomOffset, lightMode = LightMode.Block, selectionBoxes = PlantBox,
        tileTop = tile, tileBottom = tile, tileSide = tile
    };

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

    static BlockInfo Model(Element[] elements, bool collidable, bool cullSameType)
    {
        var boxes = new Box[elements.Length];
        for (int i = 0; i < elements.Length; i++)
            boxes[i] = new Box(elements[i].min, elements[i].max);

        return new BlockInfo
        {
            shape = BlockShape.Model, hasMesh = true, opaque = false,
            collidable = collidable, cullSameType = cullSameType, flushMask = FlushMask(elements),
            elements = elements, collisionBoxes = collidable ? boxes : null, selectionBoxes = boxes
        };
    }

    // ------------------------------------------------------------------
    // Accès
    // ------------------------------------------------------------------

    public static BlockInfo Get(BlockType type) => infos[(int)type];

    // Comme Get, mais SANS copier la structure (à privilégier dans les boucles chaudes)
    public static ref readonly BlockInfo GetRef(BlockType type) => ref infos[(int)type];

    // Lectures rapides : on lit UN champ directement dans le tableau, sans copier toute la structure.
    // (Pas de table séparée : elle pourrait ne plus correspondre aux infos des blocs.)
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
        var info = infos[(int)type];
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
        float size = 1f / AtlasTilesPerRow;
        int col = tile % AtlasTilesPerRow;
        int row = tile / AtlasTilesPerRow;

        // Marge minuscule contre les coutures (voir UvInsetTexels)
        float pad = UvInsetTexels / (AtlasTilesPerRow * TilePixels);

        float u0 = col * size + pad;
        float u1 = (col + 1) * size - pad;
        // En Unity, v = 0 est en BAS de la texture, on inverse donc la ligne
        float v1 = 1f - row * size - pad;
        float v0 = 1f - (row + 1) * size + pad;

        return new Vector2(Mathf.Lerp(u0, u1, u), Mathf.Lerp(v0, v1, v));
    }
}
