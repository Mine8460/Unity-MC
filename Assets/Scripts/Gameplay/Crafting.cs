using System.Collections.Generic;
using UnityEngine;

// Recettes d'artisanat, comme Minecraft : une FORME dans la grille. Elle peut être placée n'importe où dans la
// grille, et aussi en miroir (gauche-droite). Une recette de 3 de large ou de haut demande l'établi (grille 3 x 3).
public static class Crafting
{
    public sealed class Recipe
    {
        public int width, height;
        public ItemType[] cells;   // rangée par rangée, de haut en bas ; None = case vide
        public ItemStack result;
    }

    static readonly List<Recipe> recipes = new List<Recipe>();

    // Lettre de la forme -> objet attendu
    readonly struct Key
    {
        public readonly char letter;
        public readonly ItemType item;
        public Key(char letter, ItemType item) { this.letter = letter; this.item = item; }
    }

    static Key K(char letter, ItemType item) => new Key(letter, item);

    public static IReadOnlyList<Recipe> All => recipes;

    static Crafting()
    {
        ItemType log = ItemDatabase.FromBlock(BlockType.Log);
        ItemType planks = ItemDatabase.FromBlock(BlockType.Planks);
        ItemType stone = ItemDatabase.FromBlock(BlockType.Stone);

        // Base
        Shaped(BlockType.Planks, 4, new[] { "L" }, K('L', log));
        Shaped(ItemType.Stick, 4, new[] { "P", "P" }, K('P', planks));
        Shaped(BlockType.CraftingTable, 1, new[] { "PP", "PP" }, K('P', planks));
        Shaped(BlockType.Torch, 4, new[] { "C", "S" }, K('C', ItemType.Coal), K('S', ItemType.Stick));
        Shaped(BlockType.StoneSlab, 6, new[] { "SSS" }, K('S', stone));

        // Four : un anneau de pierre autour d'un trou
        Shaped((BlockType)FurnaceIds.Block, 1, new[] { "SSS", "S S", "SSS" }, K('S', stone));

        // Redstone
        ItemType dust = ItemType.RedstoneDust;
        ItemType redstoneTorch = ItemDatabase.FromBlock((BlockType)Redstone.TorchLit);
        Shaped((BlockType)Redstone.TorchLit, 1, new[] { "R", "S" }, K('R', dust), K('S', ItemType.Stick));
        Shaped((BlockType)Redstone.LeverOff, 1, new[] { "S", "C" }, K('S', ItemType.Stick), K('C', stone));
        Shaped((BlockType)Redstone.ButtonOff, 1, new[] { "C" }, K('C', stone));
        Shaped((BlockType)Redstone.Block, 1, new[] { "RRR", "RRR", "RRR" }, K('R', dust));
        Shaped(dust, 9, new[] { "B" }, K('B', ItemDatabase.FromBlock((BlockType)Redstone.Block)));
        // Lampe : de la poussière autour d'un bloc de verre (Minecraft demande de la pierre lumineuse)
        Shaped((BlockType)Redstone.Lamp, 1, new[] { " R ", "RGR", " R " }, K('R', dust), K('G', ItemDatabase.FromBlock(BlockType.Glass)));
        Shaped(ItemType.Repeater, 1, new[] { "TRT", "SSS" }, K('T', redstoneTorch), K('R', dust), K('S', stone));

        // Outils : bois (planches), pierre, fer, or, diamant — dans l'ordre des outils de ItemType
        ItemType[] materials = { planks, stone, ItemType.IronIngot, ItemType.GoldIngot, ItemType.Diamond };
        for (int m = 0; m < materials.Length; m++)
        {
            Key M = K('M', materials[m]), S = K('S', ItemType.Stick);
            Shaped(Offset(ItemType.WoodenPickaxe, m), 1, new[] { "MMM", " S ", " S " }, M, S);
            Shaped(Offset(ItemType.WoodenAxe, m),     1, new[] { "MM", "MS", " S" }, M, S);
            Shaped(Offset(ItemType.WoodenShovel, m),  1, new[] { "M", "S", "S" }, M, S);
        }

        LoadFiles();
    }

    public const string ResourcesFolder = "Recipes";

    // Recettes des fichiers (Assets/Resources/Recipes/*.asset). Une recette-fichier REMPLACE les recettes du code
    // qui donnent le même objet : on peut donc modifier une recette existante, ou en ajouter de nouvelles.
    static void LoadFiles()
    {
        var fromFiles = new List<Recipe>();
        foreach (RecipeDefinition d in Resources.LoadAll<RecipeDefinition>(ResourcesFolder))
        {
            if (d.TryBuild(out Recipe r, out string error)) fromFiles.Add(r);
            else Debug.LogWarning($"Recette « {d.name} » ignorée : {error}.", d);
        }
        if (fromFiles.Count == 0) return;

        var replaced = new HashSet<ItemType>();
        foreach (Recipe r in fromFiles) replaced.Add(r.result.type);
        recipes.RemoveAll(r => replaced.Contains(r.result.type));
        recipes.AddRange(fromFiles);
    }

    static ItemType Offset(ItemType first, int m) => (ItemType)((int)first + m);

    static void Shaped(BlockType result, int count, string[] rows, params Key[] keys) =>
        Shaped(ItemDatabase.FromBlock(result), count, rows, keys);

    static void Shaped(ItemType result, int count, string[] rows, params Key[] keys)
    {
        var recipe = new Recipe
        {
            width = rows[0].Length,
            height = rows.Length,
            result = new ItemStack(result, count),
        };
        recipe.cells = new ItemType[recipe.width * recipe.height];

        for (int y = 0; y < recipe.height; y++)
        for (int x = 0; x < recipe.width; x++)
        {
            char c = rows[y][x];
            ItemType item = ItemType.None;
            foreach (var k in keys)
                if (k.letter == c) item = k.item;
            recipe.cells[y * recipe.width + x] = item;
        }

        recipes.Add(recipe);
    }

    // Résultat de la grille (size x size, rangée par rangée), ou une pile vide si rien ne correspond
    public static ItemStack Match(ItemStack[] grid, int size)
    {
        // Rectangle des cases occupées
        int minX = size, minY = size, maxX = -1, maxY = -1;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (grid[y * size + x].IsEmpty) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return default;

        int w = maxX - minX + 1, h = maxY - minY + 1;
        foreach (Recipe r in recipes)
        {
            if (r.width != w || r.height != h) continue;
            if (Matches(r, grid, size, minX, minY, false) || Matches(r, grid, size, minX, minY, true))
                return r.result;
        }
        return default;
    }

    static bool Matches(Recipe r, ItemStack[] grid, int size, int ox, int oy, bool mirror)
    {
        for (int y = 0; y < r.height; y++)
        for (int x = 0; x < r.width; x++)
        {
            int rx = mirror ? r.width - 1 - x : x;
            ItemStack s = grid[(oy + y) * size + ox + x];
            ItemType have = s.IsEmpty ? ItemType.None : s.type;
            if (have != r.cells[y * r.width + rx]) return false;
        }
        return true;
    }
}
