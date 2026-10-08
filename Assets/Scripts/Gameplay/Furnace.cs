using System;
using System.Collections.Generic;
using UnityEngine;

// Numéros des blocs du four (fixes : écrits dans les sauvegardes)
public static class FurnaceIds
{
    public const int Block = 100;      // four éteint
    public const int Lit = 101;        // four allumé (lumière + flammes sur la face)

    public static bool IsFurnace(BlockType t) => (int)t == Block || (int)t == Lit;
}

// Le contenu d'un four : ce qu'on y met, et où en est la cuisson. Un four par bloc (voir World.Furnace.cs).
[Serializable]
public class FurnaceData
{
    public ItemStack input, fuel, output;
    public float burnLeft;    // secondes de combustion restantes
    public float burnTotal;   // durée du combustible en cours (pour la barre de flamme)
    public float cook;        // secondes de cuisson de l'objet en cours

    public bool IsBurning => burnLeft > 0f;
    public float BurnFraction => burnTotal > 0f ? Mathf.Clamp01(burnLeft / burnTotal) : 0f;

    public float CookFraction
    {
        get
        {
            if (!Smelting.Find(input, out Smelting.Recipe r)) return 0f;
            return Mathf.Clamp01(cook / r.time);
        }
    }

    public bool IsEmpty => input.IsEmpty && fuel.IsEmpty && output.IsEmpty && burnLeft <= 0f;

    // Fait avancer le four de dt secondes
    public void Tick(float dt)
    {
        bool canSmelt = Smelting.Find(input, out Smelting.Recipe recipe) && HasRoomFor(recipe);

        if (burnLeft > 0f) burnLeft = Mathf.Max(0f, burnLeft - dt);

        // On allume un nouveau combustible seulement s'il y a quelque chose à cuire
        if (canSmelt && burnLeft <= 0f && !fuel.IsEmpty)
        {
            float time = Smelting.FuelTime(fuel.type);
            if (time > 0f)
            {
                burnLeft = burnTotal = time;
                fuel.count--;
                if (fuel.count <= 0) fuel = default;
            }
        }

        if (canSmelt && burnLeft > 0f)
        {
            cook += dt;
            if (cook >= recipe.time)
            {
                cook = 0f;
                input.count--;
                if (input.count <= 0) input = default;

                if (output.IsEmpty) output = new ItemStack(recipe.result.type, recipe.result.count);
                else output.count += recipe.result.count;
            }
        }
        else if (cook > 0f)
        {
            cook = Mathf.Max(0f, cook - dt * 2f); // la cuisson retombe si on la laisse refroidir
        }
    }

    bool HasRoomFor(Smelting.Recipe r)
    {
        if (output.IsEmpty) return true;
        return output.type == r.result.type && output.count + r.result.count <= ItemDatabase.MaxStack(output.type);
    }
}

// Les recettes de cuisson et les combustibles. Les recettes en fichiers (Assets/Resources/Smelting/*.asset,
// voir SmeltingDefinition) remplacent celles du code qui ont le même objet de départ.
public static class Smelting
{
    public sealed class Recipe
    {
        public ItemType input;
        public ItemStack result;
        public float time = 10f;
    }

    public const string ResourcesFolder = "Smelting";

    static readonly List<Recipe> recipes = new List<Recipe>();
    static readonly Dictionary<ItemType, float> fuels = new Dictionary<ItemType, float>();

    public static IReadOnlyList<Recipe> All => recipes;

    static Smelting()
    {
        Add(ItemDatabase.FromBlock(BlockType.IronOre), ItemType.IronIngot, 1, 10f);
        Add(ItemDatabase.FromBlock(BlockType.GoldOre), ItemType.GoldIngot, 1, 10f);
        Add(ItemDatabase.FromBlock(BlockType.Sand), ItemDatabase.FromBlock(BlockType.Glass), 1, 10f);

        fuels[ItemType.Coal] = 80f;
        fuels[ItemDatabase.FromBlock(BlockType.Log)] = 15f;
        fuels[ItemDatabase.FromBlock(BlockType.Planks)] = 15f;
        fuels[ItemType.Stick] = 5f;

        // Objets-fichiers : leur durée de combustion
        foreach (ItemDefinition d in Resources.LoadAll<ItemDefinition>(ItemDatabase.ResourcesFolder))
            if (d.fuelTime > 0f && (int)d.id > ItemDatabase.MaxBlockId) fuels[(ItemType)d.id] = d.fuelTime;

        // Recettes-fichiers
        foreach (SmeltingDefinition d in Resources.LoadAll<SmeltingDefinition>(ResourcesFolder))
        {
            ItemType input = d.input.Resolve();
            ItemType output = d.result.Resolve();
            if (input == ItemType.None || output == ItemType.None)
            {
                Debug.LogWarning($"Recette de cuisson « {d.name} » ignorée : objet de départ ou résultat vide.", d);
                continue;
            }
            recipes.RemoveAll(r => r.input == input);
            Add(input, output, Mathf.Max(1, d.resultCount), Mathf.Max(0.5f, d.cookTime));
        }
    }

    static void Add(ItemType input, ItemType result, int count, float time)
    {
        recipes.Add(new Recipe { input = input, result = new ItemStack(result, count), time = time });
    }

    public static bool Find(ItemStack input, out Recipe recipe)
    {
        recipe = null;
        if (input.IsEmpty) return false;
        for (int i = 0; i < recipes.Count; i++)
            if (recipes[i].input == input.type) { recipe = recipes[i]; return true; }
        return false;
    }

    public static float FuelTime(ItemType type) => fuels.TryGetValue(type, out float t) ? t : 0f;
    public static bool IsFuel(ItemType type) => FuelTime(type) > 0f;
}
