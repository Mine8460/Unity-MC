#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Migration : transforme les objets et les recettes définis dans le code en fichiers.
// Menu Voxel > Exporter les objets et recettes du code. À lancer UNE fois (relancer ne remplace jamais un fichier existant).
//   - Assets/Resources/Items/<Objet>.asset         pour chaque objet du code (charbon, lingots, outils, nourriture...) ;
//   - Assets/Voxel/Textures/Items/<Objet>.png      son icône, à repeindre quand tu veux ;
//   - Assets/Resources/Recipes/<Recette>.asset     pour chaque recette du code.
// Ensuite tu peux tout modifier dans l'inspecteur, sans toucher au code.
public static class ItemAssetExporter
{
    const string ItemFolder = "Assets/Resources/Items";
    const string RecipeFolder = "Assets/Resources/Recipes";
    const string IconFolder = "Assets/Voxel/Textures/Items";

    [MenuItem("Voxel/Exporter les objets et recettes du code")]
    static void Export()
    {
        CreateFolder(ItemFolder);
        CreateFolder(RecipeFolder);
        CreateFolder(IconFolder);

        // Blocs-fichiers par numéro (pour les ingrédients qui sont des blocs)
        var blockById = new Dictionary<int, BlockDefinition>();
        foreach (BlockDefinition b in Resources.LoadAll<BlockDefinition>(BlockDatabase.ResourcesFolder))
            blockById[(int)b.id] = b;

        // ----- Objets -----
        var itemByType = new Dictionary<ItemType, ItemDefinition>();
        int items = 0;
        foreach (ItemType type in ItemDatabase.BuiltinItems)
        {
            string path = ItemFolder + "/" + type + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (existing != null) { itemByType[type] = existing; continue; }

            ItemInfo info = ItemDatabase.Get(type);
            var def = ScriptableObject.CreateInstance<ItemDefinition>();
            def.id = type;
            def.displayName = info.name;
            def.iconColor = info.iconColor;
            def.maxStack = Mathf.Clamp(info.maxStack, 1, 64);
            def.tool = info.tool;
            def.toolLevel = info.toolLevel;
            def.toolSpeed = info.toolSpeed;
            def.durability = info.durability;
            def.food = info.food;
            def.saturation = info.saturation;
            def.icon = ExportIcon(type);

            AssetDatabase.CreateAsset(def, path);
            itemByType[type] = def;
            items++;
        }
        AssetDatabase.SaveAssets();

        // ----- Recettes -----
        int recipes = 0;
        var used = new Dictionary<string, int>();
        foreach (Crafting.Recipe r in Crafting.All)
        {
            string baseName = "Recette_" + r.result.type;
            used.TryGetValue(baseName, out int n);
            used[baseName] = n + 1;
            string path = RecipeFolder + "/" + baseName + (n > 0 ? "_" + (n + 1) : "") + ".asset";
            if (AssetDatabase.LoadAssetAtPath<RecipeDefinition>(path) != null) continue;

            // Une lettre par ingrédient différent
            var letters = new Dictionary<ItemType, char>();
            var ingredients = new List<RecipeIngredient>();
            var rows = new string[r.height];
            for (int y = 0; y < r.height; y++)
            {
                var row = new char[r.width];
                for (int x = 0; x < r.width; x++)
                {
                    ItemType cell = r.cells[y * r.width + x];
                    if (cell == ItemType.None) { row[x] = '.'; continue; }

                    if (!letters.TryGetValue(cell, out char letter))
                    {
                        letter = (char)('A' + letters.Count);
                        letters[cell] = letter;
                        ingredients.Add(new RecipeIngredient { letter = letter.ToString(), item = MakeRef(cell, itemByType, blockById) });
                    }
                    row[x] = letter;
                }
                rows[y] = new string(row);
            }

            var def = ScriptableObject.CreateInstance<RecipeDefinition>();
            def.pattern = rows;
            def.ingredients = ingredients.ToArray();
            def.result = MakeRef(r.result.type, itemByType, blockById);
            def.resultCount = r.result.count;
            AssetDatabase.CreateAsset(def, path);
            recipes++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Export terminé",
            $"{items} objets et {recipes} recettes créés.\n\nObjets : {ItemFolder}\nRecettes : {RecipeFolder}\nIcônes : {IconFolder}", "OK");
    }

    static ItemRef MakeRef(ItemType type, Dictionary<ItemType, ItemDefinition> items, Dictionary<int, BlockDefinition> blocks)
    {
        var r = new ItemRef();
        if (items.TryGetValue(type, out ItemDefinition item)) r.item = item;
        else if (ItemDatabase.IsBlock(type) && blocks.TryGetValue((int)type, out BlockDefinition block)) r.block = block;
        else r.builtin = type;
        return r;
    }

    // Écrit l'icône (celle du jeu) dans un PNG, réglé en pixels nets
    static Texture2D ExportIcon(ItemType type)
    {
        string path = IconFolder + "/" + type + ".png";
        if (!File.Exists(path))
        {
            ItemIcons.GetPixels(type, out Color32[] pixels, out int w, out int h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static void CreateFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        CreateFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
#endif
