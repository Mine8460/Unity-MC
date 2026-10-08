using System;
using System.Collections.Generic;
using UnityEngine;

// Référence à un objet : un objet-fichier, un bloc-fichier, ou (en dernier recours) un objet du code.
[Serializable]
public struct ItemRef
{
    [Tooltip("Un objet que tu as créé (Voxel > Objet)")]
    public ItemDefinition item;
    [Tooltip("Un bloc que tu as créé (Voxel > Bloc)")]
    public BlockDefinition block;
    [Tooltip("Objet du code (charbon, lingot, bâton...) : utilisé seulement si les deux champs du dessus sont vides")]
    public ItemType builtin;

    public ItemType Resolve()
    {
        if (item != null) return (ItemType)item.id;
        if (block != null) return (ItemType)block.id;
        return builtin;
    }
}

[Serializable]
public struct RecipeIngredient
{
    [Tooltip("Une lettre, utilisée dans le motif")]
    public string letter;
    public ItemRef item;
}

// Une recette d'artisanat dans un FICHIER. Créer : Project > Create > Voxel > Recette.
// Range-la dans Assets/Resources/Recipes/. Une recette du fichier REMPLACE celles du code qui donnent le même objet.
[CreateAssetMenu(menuName = "Voxel/Recette", fileName = "NouvelleRecette", order = 2)]
public class RecipeDefinition : ScriptableObject
{
    [Tooltip("Le motif, rangée par rangée (3 rangées max, 3 lettres max). Un espace ou un point = case vide.\n" +
             "Exemple pioche : « MMM », « .S. », « .S. »")]
    public string[] pattern = new string[] { "M", "S" };

    [Tooltip("Ce que chaque lettre représente")]
    public RecipeIngredient[] ingredients = new RecipeIngredient[0];

    public ItemRef result;
    [Range(1, 64)] public int resultCount = 1;

    // Construit la recette ; error explique ce qui ne va pas
    public bool TryBuild(out Crafting.Recipe recipe, out string error)
    {
        recipe = null;
        error = null;

        if (pattern == null || pattern.Length == 0 || pattern.Length > 3) { error = "le motif doit avoir 1 à 3 rangées"; return false; }

        int width = 0;
        foreach (string row in pattern)
        {
            if (row == null) { error = "rangée vide dans le motif"; return false; }
            width = Mathf.Max(width, row.Length);
        }
        if (width == 0 || width > 3) { error = "le motif doit faire 1 à 3 colonnes"; return false; }

        ItemType resultType = result.Resolve();
        if (resultType == ItemType.None) { error = "le résultat est vide"; return false; }

        var map = new Dictionary<char, ItemType>();
        if (ingredients != null)
            foreach (RecipeIngredient ing in ingredients)
                if (!string.IsNullOrEmpty(ing.letter)) map[ing.letter[0]] = ing.item.Resolve();

        var r = new Crafting.Recipe
        {
            width = width,
            height = pattern.Length,
            result = new ItemStack(resultType, resultCount),
        };
        r.cells = new ItemType[r.width * r.height];

        bool any = false;
        for (int y = 0; y < r.height; y++)
        for (int x = 0; x < r.width; x++)
        {
            char c = x < pattern[y].Length ? pattern[y][x] : ' ';
            if (c == ' ' || c == '.') continue;

            if (!map.TryGetValue(c, out ItemType item) || item == ItemType.None)
            {
                error = "la lettre « " + c + " » n'a pas d'ingrédient";
                return false;
            }
            r.cells[y * r.width + x] = item;
            any = true;
        }

        if (!any) { error = "le motif est vide"; return false; }

        recipe = r;
        return true;
    }
}
