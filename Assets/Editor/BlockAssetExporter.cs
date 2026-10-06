#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Migration : transforme tes blocs actuels (définis dans le code) en fichiers BlockDefinition.
// Menu Voxel > Exporter les blocs actuels en fichiers. À lancer UNE fois, AVANT de remplacer BlockType.cs.
//   - découpe l'atlas actuel en images (Assets/Voxel/Textures/Blocks/tile_N.png, et _height, _metallic, _emission) ;
//   - crée Assets/Resources/Blocks/<Bloc>.asset pour chaque bloc de l'enum BlockType, propriétés comprises.
// Un fichier de bloc qui existe déjà n'est jamais écrasé.
// Les infos des blocs sont lues par réflexion : l'outil marche avec l'ancien comme avec le nouveau BlockType.cs.
public static class BlockAssetExporter
{
    const string TextureFolder = "Assets/Voxel/Textures/Blocks";
    const string BlockFolder = "Assets/Resources/Blocks";

    [MenuItem("Voxel/Exporter les blocs actuels en fichiers")]
    static void Export()
    {
        // L'atlas actuel : celui du matériau des chunks du World de la scène ouverte
        World world = Object.FindObjectOfType<World>();
        Material material = world != null
            ? typeof(World).GetField("chunkMaterial", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(world) as Material
            : null;
        Texture2D atlas = material != null ? material.GetTexture("_BaseMap") as Texture2D : null;
        if (atlas == null)
        {
            EditorUtility.DisplayDialog("Exporter les blocs",
                "Ouvre la scène du jeu : il faut un World avec son matériau de chunks (et son atlas dans « Base Map »).", "OK");
            return;
        }

        int perRow = ReadInt("AtlasTilesPerRow", 4);
        int tilePx = atlas.width / perRow;
        Color32[] albedo = ReadPixels(atlas, false);
        Color32[] height = ReadPixels(material.GetTexture("_HeightMap") as Texture2D, true, atlas.width);
        Color32[] metallic = ReadPixels(material.GetTexture("_MetallicGlossMap") as Texture2D, true, atlas.width);
        Color32[] emission = ReadPixels(material.GetTexture("_EmissionMap") as Texture2D, false, atlas.width);

        CreateFolder(TextureFolder);
        CreateFolder(BlockFolder);

        var tiles = new TileSlicer
        {
            albedo = albedo, height = height, metallic = metallic, emission = emission,
            atlasWidth = atlas.width, perRow = perRow, tilePx = tilePx,
        };

        var created = new Dictionary<BlockType, BlockDefinition>();
        var dropTargets = new Dictionary<BlockDefinition, BlockType>();
        int skipped = 0;

        foreach (BlockType type in System.Enum.GetValues(typeof(BlockType)))
        {
            if (type == BlockType.Air) continue;

            string path = $"{BlockFolder}/{type}.asset";
            if (AssetDatabase.LoadAssetAtPath<BlockDefinition>(path) != null) { skipped++; continue; }

            object info = BlockDatabase.Get(type); // structure BlockInfo (ancienne ou nouvelle)
            var def = ScriptableObject.CreateInstance<BlockDefinition>();
            def.id = type;
            def.displayName = System.Text.RegularExpressions.Regex.Replace(type.ToString(), "(?<!^)([A-Z])", " $1");

            def.shape = (BlockShape)Get<int>(info, "shape", 0);
            def.opaque = Get(info, "opaque", false);
            def.collidable = Get(info, "collidable", false);
            def.cullSameType = Get(info, "cullSameType", false);
            def.randomOffset = Get(info, "randomOffset", false);
            def.replaceable = Get(info, "replaceable", false);
            def.isSoil = Get(info, "isSoil", false);
            def.gravity = Get(info, "gravity", false);
            def.support = (SupportRule)Get<int>(info, "support", 0);
            def.attachDir = Get(info, "attachDir", Vector3Int.zero);
            def.emission = Get<int>(info, "emission", 0);
            def.lightFilter = Get<int>(info, "lightFilter", 0);
            def.lightMode = (LightMode)Get<int>(info, "lightMode", 0);
            def.breakTime = Get(info, "breakTime", 1f);
            def.tool = (ToolKind)Get<int>(info, "tool", 0);
            def.harvestLevel = Get(info, "harvestLevel", 0);
            def.dropItem = (ItemType)Get<int>(info, "dropItem", 0);
            def.dropsNothing = Get(info, "dropsNothing", false);
            def.dropCount = Get<int>(info, "dropCount", 0);

            int top = Get(info, "tileTop", 0), bottom = Get(info, "tileBottom", 0), side = Get(info, "tileSide", 0);
            def.side = tiles.Face(side);
            def.top = top != side ? tiles.Face(top) : new FaceTextures();
            def.bottom = bottom != side ? tiles.Face(bottom) : new FaceTextures();

            // Forme « Model » en boîtes : chaque élément devient une boîte (en 16èmes de bloc)
            if (Get<object>(info, "elements", null) is System.Array elements && elements.Length > 0)
            {
                var boxes = new List<ModelBox>();
                foreach (object e in elements)
                {
                    Vector3 min = Get(e, "min", Vector3.zero), max = Get(e, "max", Vector3.one);
                    int[] t = Get<int[]>(e, "tiles", null);
                    var box = new ModelBox { from = min * 16f, to = max * 16f };
                    if (t != null && t.Length >= 3)
                    {
                        if (t[0] >= 0 && t[0] != top) box.top = tiles.Face(t[0]).albedo;
                        if (t[1] >= 0 && t[1] != bottom) box.bottom = tiles.Face(t[1]).albedo;
                        if (t[2] >= 0 && t[2] != side) box.side = tiles.Face(t[2]).albedo;
                    }
                    boxes.Add(box);
                }
                def.boxes = boxes.ToArray();
            }
            else if (def.shape == BlockShape.Model)
            {
                Debug.LogWarning($"Export : {type} est un modèle importé (Blockbench). Glisse son .json dans « Blockbench Model » " +
                                 "et ses textures dans « Blockbench Textures ».");
            }

            int dropOverride = Get<int>(info, "dropOverride", 0);
            if (dropOverride != 0) dropTargets[def] = (BlockType)dropOverride;

            AssetDatabase.CreateAsset(def, path);
            created[type] = def;
        }

        // Liens « lâche un autre bloc » (une fois tous les fichiers créés)
        foreach (var kv in dropTargets)
        {
            BlockDefinition target = created.TryGetValue(kv.Value, out var t) ? t
                : AssetDatabase.LoadAssetAtPath<BlockDefinition>($"{BlockFolder}/{kv.Value}.asset");
            kv.Key.dropBlock = target;
            EditorUtility.SetDirty(kv.Key);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Exporter les blocs",
            $"{created.Count} fichier(s) de bloc créé(s) dans {BlockFolder}, {tiles.Count} tuile(s) découpée(s) dans {TextureFolder}." +
            (skipped > 0 ? $"\n{skipped} bloc(s) avaient déjà un fichier : non modifiés." : "") +
            "\n\nTu peux maintenant remplacer BlockType.cs.", "OK");
    }

    // Découpe les tuiles de l'atlas à la demande (une seule fois chacune)
    sealed class TileSlicer
    {
        public Color32[] albedo, height, metallic, emission;
        public int atlasWidth, perRow, tilePx;
        readonly Dictionary<int, FaceTextures> cache = new Dictionary<int, FaceTextures>();

        public int Count => cache.Count;

        public FaceTextures Face(int tile)
        {
            if (tile < 0) return new FaceTextures();
            if (cache.TryGetValue(tile, out FaceTextures f)) return f;
            f = new FaceTextures
            {
                albedo = SaveTile(albedo, atlasWidth, perRow, tilePx, tile, $"tile_{tile}", false),
                height = SaveTile(height, atlasWidth, perRow, tilePx, tile, $"tile_{tile}_height", true),
                metallic = SaveTile(metallic, atlasWidth, perRow, tilePx, tile, $"tile_{tile}_metallic", true),
                emission = SaveTile(emission, atlasWidth, perRow, tilePx, tile, $"tile_{tile}_emission", false),
            };
            cache[tile] = f;
            return f;
        }
    }

    // ------------------------------------------------------------------

    static T Get<T>(object obj, string field, T fallback)
    {
        FieldInfo f = obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public);
        if (f == null) return fallback;
        object v = f.GetValue(obj);
        if (v == null) return fallback;
        if (v is T t) return t;
        try { return (T)System.Convert.ChangeType(v, typeof(T)); } catch { return fallback; }
    }

    // Constante (ancien BlockType.cs) ou propriété (nouveau) de BlockDatabase
    static int ReadInt(string name, int fallback)
    {
        System.Type db = typeof(BlockDatabase);
        FieldInfo f = db.GetField(name, BindingFlags.Static | BindingFlags.Public);
        if (f != null) return (int)f.GetValue(null);
        PropertyInfo p = db.GetProperty(name, BindingFlags.Static | BindingFlags.Public);
        return p != null ? (int)p.GetValue(null) : fallback;
    }

    // Pixels d'une texture (même non lisible), à la taille de l'atlas
    static Color32[] ReadPixels(Texture2D t, bool linear, int expectedWidth = -1)
    {
        if (t == null) return null;
        if (expectedWidth > 0 && t.width != expectedWidth)
        {
            Debug.LogWarning($"Export : la texture « {t.name} » n'a pas la taille de l'atlas : elle est ignorée.");
            return null;
        }
        RenderTexture rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32,
                                                      linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        Graphics.Blit(t, rt);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        var copy = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, linear);
        copy.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
        copy.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] pixels = copy.GetPixels32();
        Object.DestroyImmediate(copy);
        return pixels;
    }

    // Enregistre une tuile en PNG (pixels nets, sans compression) et renvoie la texture importée
    static Texture2D SaveTile(Color32[] atlas, int atlasWidth, int perRow, int tilePx, int tile, string fileName, bool linear)
    {
        if (atlas == null) return null;

        int col = tile % perRow, row = tile / perRow;
        int ox = col * tilePx, oy = (perRow - 1 - row) * tilePx; // rangée 0 en haut de l'image
        var pixels = new Color32[tilePx * tilePx];
        for (int y = 0; y < tilePx; y++)
            System.Array.Copy(atlas, (oy + y) * atlasWidth + ox, pixels, y * tilePx, tilePx);

        var tex = new Texture2D(tilePx, tilePx, TextureFormat.RGBA32, false, linear);
        tex.SetPixels32(pixels);
        tex.Apply();
        string path = $"{TextureFolder}/{fileName}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path);
        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
        {
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.sRGBTexture = !linear;
            importer.alphaIsTransparency = true;
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
