using System;
using System.Collections.Generic;
using UnityEngine;

// Tout ce qui peut aller dans l'inventaire.
//   0 à 255 : les BLOCS, avec le même numéro que BlockType (ItemType.FromBlock / ToBlock) ;
//   256 et plus : les OBJETS qui ne sont pas des blocs (charbon, lingots, outils...).
// ATTENTION : ne change jamais les valeurs existantes (elles sont écrites dans les sauvegardes).
public enum ItemType : ushort
{
    None = 0,   // = BlockType.Air

    Coal = 256,
    IronIngot,
    GoldIngot,
    Diamond,
    Stick,

    WoodenPickaxe, StonePickaxe, IronPickaxe, GoldenPickaxe, DiamondPickaxe,
    WoodenAxe, StoneAxe, IronAxe, GoldenAxe, DiamondAxe,
    WoodenShovel, StoneShovel, IronShovel, GoldenShovel, DiamondShovel,

    RedstoneDust,   // se pose en fil (bloc RedstoneDust)
    Repeater,       // se pose en répéteur (bloc Repeater)

    Apple, Bread, Steak,   // nourriture (voir ItemInfo.food)
}

// Outil qui convient à un bloc (BlockInfo.tool) ou type d'un outil (ItemInfo.tool)
public enum ToolKind : byte { None, Pickaxe, Axe, Shovel, Sword }

public struct ItemInfo
{
    public string name;        // nom affiché (infobulle)
    public int maxStack;       // 64, ou 1 pour un outil
    public ToolKind tool;
    public int toolLevel;      // 1 = bois/or, 2 = pierre, 3 = fer, 4 = diamant (comparé à BlockInfo.harvestLevel)
    public float toolSpeed;    // multiplie la vitesse de casse des blocs qui lui conviennent
    public int durability;     // nombre de blocs avant de casser (0 = pas un outil)
    public float attackDamage; // dégâts aux monstres (0 = ceux de la main : 1)
    public int food;           // points de faim rendus en mangeant (0 = pas comestible)
    public float saturation;   // réserve de satiété ajoutée (ralentit la faim)
    public Color32 iconColor;
    public Texture2D icon;     // image de l'icône (objets-fichiers) ; null = générée  // couleur de l'icône générée (tête d'outil, lingot...)
}

public static class ItemDatabase
{
    public const int MaxBlockId = 255;

    static readonly Dictionary<ItemType, ItemInfo> items = new Dictionary<ItemType, ItemInfo>();
    static readonly Dictionary<ItemType, BlockType> placedBlocks = new Dictionary<ItemType, BlockType>(); // objets-fichiers qui posent un bloc

    // Les objets définis dans le code (avant le chargement des fichiers) : sert à l'outil d'export
    public static readonly List<ItemType> BuiltinItems = new List<ItemType>();
    public const string ResourcesFolder = "Items";

    // Matériaux des outils, comme Minecraft : (nom, niveau, vitesse, durabilité, couleur de la tête)
    static readonly (string name, int level, float speed, int durability, Color32 color)[] Materials =
    {
        ("en bois",    1,  2f,   59, new Color32(184, 148,  95, 255)),
        ("en pierre",  2,  4f,  131, new Color32(140, 140, 140, 255)),
        ("en fer",     3,  6f,  250, new Color32(222, 222, 222, 255)),
        ("en or",      1, 12f,   32, new Color32(246, 211,  68, 255)),
        ("en diamant", 4,  8f, 1561, new Color32( 82, 228, 214, 255)),
    };

    static ItemDatabase()
    {
        Material(ItemType.Coal,      "Charbon",       new Color32( 40,  40,  44, 255));
        Material(ItemType.IronIngot, "Lingot de fer", new Color32(222, 222, 222, 255));
        Material(ItemType.GoldIngot, "Lingot d'or",   new Color32(246, 211,  68, 255));
        Material(ItemType.Diamond,   "Diamant",       new Color32( 82, 228, 214, 255));
        Material(ItemType.Stick,     "Bâton",         new Color32(122,  90,  46, 255));
        Material(ItemType.RedstoneDust, "Poussière de redstone", new Color32(205, 30, 24, 255));
        Material(ItemType.Repeater,     "Répéteur",              new Color32(125, 125, 125, 255));

        Food(ItemType.Apple, "Pomme",  4, 2.4f, new Color32(206, 40, 40, 255));
        Food(ItemType.Bread, "Pain",   5, 6f,   new Color32(196, 150, 78, 255));
        Food(ItemType.Steak, "Steak",  8, 12.8f, new Color32(150, 70, 50, 255));

        for (int m = 0; m < Materials.Length; m++)
        {
            Tool((ItemType)((int)ItemType.WoodenPickaxe + m), ToolKind.Pickaxe, "Pioche " + Materials[m].name, m);
            Tool((ItemType)((int)ItemType.WoodenAxe + m),     ToolKind.Axe,     "Hache " + Materials[m].name, m);
            Tool((ItemType)((int)ItemType.WoodenShovel + m),  ToolKind.Shovel,  "Pelle " + Materials[m].name, m);
        }

        BuiltinItems.AddRange(items.Keys);
        LoadFiles();
    }

    // Objets définis dans Assets/Resources/Items/*.asset (ItemDefinition) : ils s'ajoutent à ceux du code,
    // ou les remplacent s'ils ont le même numéro.
    static void LoadFiles()
    {
        ItemDefinition[] defs = Resources.LoadAll<ItemDefinition>(ResourcesFolder);
        foreach (ItemDefinition d in defs)
        {
            if ((int)d.id <= MaxBlockId || (int)d.id > ushort.MaxValue)
            {
                Debug.LogError($"Objet « {d.name} » : le numéro {d.id} doit être entre {MaxBlockId + 1} et {ushort.MaxValue}.", d);
                continue;
            }

            var type = (ItemType)d.id;
            items[type] = new ItemInfo
            {
                name = string.IsNullOrEmpty(d.displayName) ? d.name : d.displayName,
                maxStack = d.tool != ToolKind.None || d.durability > 0 ? 1 : Mathf.Clamp(d.maxStack, 1, 64),
                tool = d.tool,
                toolLevel = d.toolLevel,
                toolSpeed = d.toolSpeed,
                durability = d.durability,
                attackDamage = d.attackDamage,
                food = d.food,
                saturation = d.saturation,
                iconColor = d.iconColor,
                icon = d.icon,
            };

            if (d.placesBlock != null) placedBlocks[type] = (BlockType)d.placesBlock.id;
            else placedBlocks.Remove(type);
        }
    }

    // Le bloc qu'un objet pose : le bloc lui-même, ou (pour la poussière et le répéteur, qui ne sont pas des blocs
    // de l'inventaire) le bloc correspondant. Air = l'objet ne se pose pas.
    public static BlockType PlacedBlock(ItemType type)
    {
        if (IsBlock(type)) return ToBlock(type);
        if (type == ItemType.RedstoneDust) return (BlockType)Redstone.Dust;
        if (type == ItemType.Repeater) return (BlockType)Redstone.Repeater;
        if (placedBlocks.TryGetValue(type, out BlockType placed)) return placed;
        return BlockType.Air;
    }

    static void Material(ItemType type, string name, Color32 color)
    {
        items[type] = new ItemInfo { name = name, maxStack = 64, iconColor = color };
    }

    static void Food(ItemType type, string name, int food, float saturation, Color32 color)
    {
        items[type] = new ItemInfo { name = name, maxStack = 64, food = food, saturation = saturation, iconColor = color };
    }

    static void Tool(ItemType type, ToolKind kind, string name, int material)
    {
        var m = Materials[material];
        items[type] = new ItemInfo
        {
            name = name, maxStack = 1, tool = kind, toolLevel = m.level, toolSpeed = m.speed,
            durability = m.durability, iconColor = m.color,
            attackDamage = (kind == ToolKind.Sword ? 3f : kind == ToolKind.Axe ? 2.5f : 1f) + m.level,
        };
    }

    // ------------------------------------------------------------------
    // Blocs <-> objets
    // ------------------------------------------------------------------

    public static bool IsBlock(ItemType type) => (int)type <= MaxBlockId;
    public static ItemType FromBlock(BlockType block) => (ItemType)(int)block;
    public static BlockType ToBlock(ItemType type) => IsBlock(type) ? (BlockType)(int)type : BlockType.Air;

    public static ItemInfo Get(ItemType type)
    {
        if (items.TryGetValue(type, out ItemInfo info)) return info;
        return new ItemInfo { name = BlockName(ToBlock(type)), maxStack = 64 }; // un bloc
    }

    public static int MaxStack(ItemType type) => Get(type).maxStack;

    public static string Name(ItemType type) => Get(type).name;

    // « StoneSlabTop » -> « Stone Slab Top »
    static string BlockName(BlockType block) => BlockDatabase.Name(block); // « Display Name » du fichier du bloc

    // ------------------------------------------------------------------
    // Casse des blocs avec un outil
    // ------------------------------------------------------------------

    // Temps pour casser un bloc avec l'objet tenu en main (pile vide = à la main), comme Minecraft :
    //   - le bon outil divise le temps par sa vitesse ;
    //   - si le bloc exige un outil (harvestLevel > 0) et qu'il manque ou est trop faible, le bloc se casse
    //     3,3 fois plus lentement et ne lâche rien (canHarvest = false).
    // Retourne une valeur négative si le bloc est incassable.
    public static float BreakTime(BlockType block, ItemStack held, out bool canHarvest)
    {
        BlockInfo b = BlockDatabase.Get(block);
        canHarvest = true;
        if (b.breakTime < 0f) return -1f;

        ItemInfo tool = held.IsEmpty ? default : Get(held.type);
        bool rightTool = b.tool != ToolKind.None && tool.tool == b.tool;

        float time = b.breakTime;
        if (rightTool) time /= Mathf.Max(1f, tool.toolSpeed);

        canHarvest = b.harvestLevel <= 0 || (rightTool && tool.toolLevel >= b.harvestLevel);
        if (!canHarvest) time *= 10f / 3f;

        return time;
    }
}

// Icônes des objets qui ne sont pas des blocs. Si Assets/Resources/Items/<NomDeLObjet>.png existe
// (ex. Items/IronPickaxe.png), elle est utilisée ; sinon l'icône est générée en pixel art (16 x 16).
public static class ItemIcons
{
    public const int Size = 16;

    static readonly Dictionary<ItemType, Texture2D> cache = new Dictionary<ItemType, Texture2D>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache() => cache.Clear();

    public static Texture2D Get(ItemType type)
    {
        if (cache.TryGetValue(type, out Texture2D tex)) return tex;

        tex = ItemDatabase.Get(type).icon; // icône donnée dans le fichier de l'objet
        if (tex == null) tex = Resources.Load<Texture2D>("Items/" + type);
        if (tex == null)
        {
            tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(Generate(type));
            tex.Apply();
        }

        cache[type] = tex;
        return tex;
    }

    // Pixels de l'icône d'un objet (ligne du BAS en premier), à sa taille réelle : celle de ton image
    // Resources/Items/<Objet>.png si elle existe (même non « Read/Write »), sinon l'icône générée.
    public static void GetPixels(ItemType type, out Color32[] pixels, out int width, out int height)
    {
        Texture2D tex = Get(type);
        width = tex.width;
        height = tex.height;

        if (tex.isReadable)
        {
            pixels = tex.GetPixels32();
            return;
        }

        // Image non lisible : on la recopie dans une RenderTexture que l'on relit
        RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
        copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        copy.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        pixels = copy.GetPixels32();
        UnityEngine.Object.Destroy(copy);
    }

    // ------------------------------------------------------------------
    // Génération (fonction pure : testable hors d'Unity)
    // ------------------------------------------------------------------

    static readonly Color32 Handle = new Color32(122, 90, 46, 255);

    // Pixels de l'icône, ligne du BAS en premier (comme une texture Unity)
    public static Color32[] Generate(ItemType type)
    {
        var px = new Color32[Size * Size];
        ItemInfo info = ItemDatabase.Get(type);
        Color32 c = info.iconColor;

        switch (type)
        {
            case ItemType.Stick: DrawHandle(px, 2, 13, 12, 3); break;
            case ItemType.Coal: DrawLump(px, c); break;
            case ItemType.IronIngot:
            case ItemType.GoldIngot: DrawIngot(px, c); break;
            case ItemType.Diamond: DrawGem(px, c); break;
            case ItemType.RedstoneDust: DrawDustPile(px); break;
            case ItemType.Repeater: DrawRepeaterIcon(px); break;
            default:
                if (info.tool == ToolKind.None) { DrawLump(px, c); break; } // objet sans dessin particulier : une petite masse de sa couleur
                DrawHandle(px, 2, 13, 10, 5);
                if (info.tool == ToolKind.Pickaxe) DrawPickaxeHead(px, c);
                else if (info.tool == ToolKind.Axe) DrawAxeHead(px, c);
                else DrawShovelHead(px, c);
                break;
        }

        Outline(px);
        return px;
    }

    // (x, y) avec y = 0 en HAUT de l'image (plus simple pour dessiner)
    static void Set(Color32[] px, int x, int y, Color32 c)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Size) return;
        px[(Size - 1 - y) * Size + x] = c;
    }

    static Color32 Get(Color32[] px, int x, int y)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Size) return default;
        return px[(Size - 1 - y) * Size + x];
    }

    static Color32 Shade(Color32 c, float f) =>
        new Color32((byte)Mathf.Clamp(c.r * f, 0, 255), (byte)Mathf.Clamp(c.g * f, 0, 255), (byte)Mathf.Clamp(c.b * f, 0, 255), 255);

    // Manche en diagonale, de (x0, y0) en bas à gauche vers (x1, y1) en haut à droite, 2 pixels d'épaisseur
    static void DrawHandle(Color32[] px, int x0, int y0, int x1, int y1)
    {
        int n = x1 - x0;
        for (int t = 0; t <= n; t++)
        {
            Set(px, x0 + t, y0 - t, Handle);
            Set(px, x0 + t + 1, y0 - t, Shade(Handle, 0.75f));
        }
    }

    // Tête de pioche : un arc de cercle centré sur le bas du manche, qui croise le haut du manche
    static void DrawPickaxeHead(Color32[] px, Color32 c)
    {
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float dx = x - 2f, dy = 13f - y;           // depuis le bas du manche
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            if (dx < -0.5f || dy < -0.5f) continue;    // seulement le quart haut-droit
            if (r >= 10.6f && r < 12.6f) Set(px, x, y, r < 11.6f ? c : Shade(c, 0.7f));
        }
    }

    // Poussière de redstone : un petit tas rouge
    static void DrawDustPile(Color32[] px)
    {
        for (int y = 3; y <= 11; y++)
        for (int x = 2; x <= 13; x++)
        {
            float dx = (x - 7.5f) / 5.8f, dy = (y - 6.5f) / 3.8f;
            if (dx * dx + dy * dy > 1f) continue;

            float shade = 0.75f + 0.25f * ((x * 7 + y * 13) % 5) / 4f + (y - 3) * 0.02f;
            Set(px, x, y, new Color32((byte)Mathf.Min(255, 225 * shade), (byte)(28 * shade), (byte)(20 * shade), 255));
        }
    }

    // Répéteur : une dalle grise avec deux petites torches rouges
    static void DrawRepeaterIcon(Color32[] px)
    {
        for (int y = 3; y <= 6; y++)
        for (int x = 1; x <= 14; x++)
            Set(px, x, y, y == 6 ? new Color32(170, 170, 170, 255) : new Color32(120, 120, 120, 255));

        foreach (int tx in new[] { 4, 10 })
        for (int y = 7; y <= 11; y++)
        for (int x = tx; x <= tx + 1; x++)
            Set(px, x, y, y >= 10 ? new Color32(235, 40, 30, 255) : new Color32(122, 88, 48, 255));
    }

    // Tête de hache : une lame large d'un seul côté du haut du manche (vers le haut-gauche), au tranchant plus clair
    static void DrawAxeHead(Color32[] px, Color32 c)
    {
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float u = (x - 2) - (13 - y);              // écart au manche : négatif = côté haut-gauche
            float t = ((x - 2) + (13 - y)) * 0.5f;     // position le long du manche
            if (t < 6.5f || t > 11f || u < -5.5f || u > -1f) continue;

            Color32 col = u <= -4.5f ? Shade(c, 1.15f) : (u <= -2.5f ? c : Shade(c, 0.75f));
            Set(px, x, y, col);
        }
    }

    // Tête de pelle : une lame arrondie au bout du manche
    static void DrawShovelHead(Color32[] px, Color32 c)
    {
        for (int y = 0; y <= 8; y++)
        for (int x = 7; x <= 15; x++)
        {
            // distance au segment (10, 5) -> (13, 2), dans le prolongement du manche
            float t = Mathf.Clamp01(((x - 10f) * 3f + (5f - y) * 3f) / 18f);
            float sx = 10f + 3f * t, sy = 5f - 3f * t;
            float d = Mathf.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
            if (d < 2.6f) Set(px, x, y, d < 1.4f ? c : Shade(c, 0.75f));
        }
    }

    // Bloc de charbon : une boule irrégulière avec quelques reflets
    static void DrawLump(Color32[] px, Color32 c)
    {
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float dx = x - 7.5f, dy = y - 8f;
            float r = Mathf.Sqrt(dx * dx + dy * dy) + 0.8f * Mathf.Sin(x * 1.7f + y * 0.9f);
            if (r > 5.6f) continue;
            bool glint = ((x * 7 + y * 13) % 11) == 0 && r < 4.5f;
            Set(px, x, y, glint ? new Color32(110, 110, 120, 255) : (dx + dy < 0 ? Shade(c, 1.4f) : c));
        }
    }

    // Lingot : une barre vue un peu de dessus (dessus clair, face avant plus sombre)
    static void DrawIngot(Color32[] px, Color32 c)
    {
        for (int y = 5; y <= 11; y++)
        for (int x = 1; x <= 14; x++)
        {
            int left = 1 + (11 - y) / 2, right = 14 - (y < 8 ? (8 - y) : 0);
            if (x < left || x > right) continue;
            Set(px, x, y, y <= 7 ? Shade(c, 1.12f) : Shade(c, 0.82f));
        }
    }

    // Diamant : une gemme à facettes
    static void DrawGem(Color32[] px, Color32 c)
    {
        for (int y = 2; y <= 13; y++)
        for (int x = 1; x <= 14; x++)
        {
            float dx = Mathf.Abs(x - 7.5f);
            bool inside = y <= 5 ? dx <= 3.5f + (y - 2) : dx <= 6.5f - (y - 5) * 0.8f;
            if (!inside) continue;
            Color32 col = y <= 5 ? Shade(c, 1.25f) : (x < 8 ? c : Shade(c, 0.75f));
            if ((x == 5 && y == 4) || (x == 6 && y == 3)) col = new Color32(240, 255, 255, 255); // reflet
            Set(px, x, y, col);
        }
    }

    // Contour sombre autour de la forme, comme les objets de Minecraft
    static void Outline(Color32[] px)
    {
        var outline = new List<(int x, int y, Color32 c)>();
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (Get(px, x, y).a != 0) continue;
            Color32 n = Get(px, x + 1, y);
            if (n.a == 0) n = Get(px, x - 1, y);
            if (n.a == 0) n = Get(px, x, y + 1);
            if (n.a == 0) n = Get(px, x, y - 1);
            if (n.a != 0) outline.Add((x, y, Shade(n, 0.3f)));
        }
        foreach (var o in outline) Set(px, o.x, o.y, o.c);
    }
}
