using System.Collections.Generic;
using UnityEngine;

// Modèle 3D d'un objet, comme dans Minecraft : son icône est « extrudée ». Chaque pixel opaque devient un petit cube
// dont l'épaisseur est 1/16 de la largeur de l'objet (1 pixel pour une icône de 16 x 16) :
//   - une face avant et une face arrière portent l'image ;
//   - sur chaque bord d'un pixel qui touche du vide, un côté de la couleur de ce pixel ferme le volume.
// Le mesh est construit UNE fois par type d'objet, puis partagé. Il est centré sur l'origine et fait 1 de large :
// on le met à l'échelle voulue (objet au sol : 0,45 ; objet tenu en main : ce que tu veux).
//
// POUR L'OBJET TENU EN MAIN (vue à la première personne), le même modèle sert :
//     GameObject model = ItemModel.Create(handPivot, world, stack, size);
// Il suffit de placer / tourner / mettre à l'échelle ce « handPivot » (enfant de la caméra) comme tu veux, et de
// détruire puis recréer `model` quand l'objet sélectionné change. Blocs et objets sont traités pareil.
public static class ItemModel
{
    // Épaisseur du modèle, en fraction de sa largeur (1/16 : un pixel d'une icône de 16 x 16)
    public const float Thickness = 1f / 16f;

    // Pixels dont l'alpha atteint ce seuil sont opaques (celui du shader : « clip(alpha - 0,5) »)
    const byte OpaqueAlpha = 128;

    static readonly Dictionary<ItemType, Mesh> cache = new Dictionary<ItemType, Mesh>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache() => cache.Clear();

    // Mesh de l'objet (partagé). Les blocs n'en ont pas : ils utilisent leur propre mesh (voir Create).
    public static Mesh GetMesh(ItemType type)
    {
        if (cache.TryGetValue(type, out Mesh mesh)) return mesh;

        ItemIcons.GetPixels(type, out Color32[] pixels, out int width, out int height);
        ItemMeshData data = Build(pixels, width, height);

        mesh = new Mesh { name = "Item " + type };
        mesh.vertices = data.vertices.ToArray();
        mesh.normals = data.normals.ToArray();
        mesh.uv = data.uvs.ToArray();
        mesh.colors32 = data.colors.ToArray();
        mesh.triangles = data.triangles.ToArray();
        mesh.RecalculateBounds();

        cache[type] = mesh;
        return mesh;
    }

    // Crée l'objet 3D d'une pile, enfant de `parent`, CENTRÉ sur l'origine du parent, de `size` de large.
    //   - un bloc : son vrai mesh, en petit ;
    //   - un objet : son icône extrudée.
    public static GameObject Create(Transform parent, World world, ItemStack stack, float size, bool castShadows = false)
    {
        var model = new GameObject("Model");
        model.transform.SetParent(parent, false);
        model.transform.localScale = Vector3.one * size;

        var renderer = model.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = castShadows ? UnityEngine.Rendering.ShadowCastingMode.On
                                                 : UnityEngine.Rendering.ShadowCastingMode.Off;

        if (stack.IsBlock)
        {
            // Le mesh d'un bloc va de (0,0,0) à (1,1,1) : on le recentre
            model.transform.localPosition = Vector3.one * (-size * 0.5f);
            model.AddComponent<MeshFilter>().sharedMesh = world.GetBlockMesh(stack.Block);
            renderer.sharedMaterial = world.ChunkMaterial;
        }
        else
        {
            model.AddComponent<MeshFilter>().sharedMesh = GetMesh(stack.type);
            renderer.sharedMaterial = world.FlatItemMaterial;

            // La texture de l'objet (son icône) remplace l'atlas, sans créer un matériau par objet
            var block = new MaterialPropertyBlock();
            block.SetTexture("_BaseMap", ItemIcons.Get(stack.type));
            renderer.SetPropertyBlock(block);
        }
        return model;
    }

    // ------------------------------------------------------------------
    // Construction du mesh (fonction pure : testable hors d'Unity)
    // ------------------------------------------------------------------

    public sealed class ItemMeshData
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Vector3> normals = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<Color32> colors = new List<Color32>();
        public readonly List<int> triangles = new List<int>();

        public int QuadCount => vertices.Count / 4;
    }

    // pixels : ligne du BAS en premier (comme une texture Unity). thickness : épaisseur, en fraction de la largeur.
    public static ItemMeshData Build(Color32[] pixels, int width, int height, float thickness = Thickness)
    {
        var data = new ItemMeshData();

        // Un pixel fait 1/n de bloc ; l'objet est centré (largeur 1 pour une icône carrée)
        float n = Mathf.Max(width, height);
        float pixel = 1f / n;
        float x0 = -width * 0.5f * pixel, x1 = width * 0.5f * pixel;
        float y0 = -height * 0.5f * pixel, y1 = height * 0.5f * pixel;
        float halfT = thickness * 0.5f;

        // Éclairage : pleine lumière du ciel, sans occlusion ; alpha 0 = éclairage « Pixel » du shader
        var color = new Color32(0, 255, 255, 0);

        // Face avant (regarde -Z) et face arrière (regarde +Z) : l'image entière. Les pixels transparents sont
        // supprimés par le shader (alpha clipping). Vue de derrière, l'image est en miroir, comme dans Minecraft.
        AddQuad(data,
                new Vector3(x0, y0, -halfT), new Vector3(x0, y1, -halfT), new Vector3(x1, y1, -halfT), new Vector3(x1, y0, -halfT),
                Vector3.back,
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), color);
        AddQuad(data,
                new Vector3(x1, y0, halfT), new Vector3(x1, y1, halfT), new Vector3(x0, y1, halfT), new Vector3(x0, y0, halfT),
                Vector3.forward,
                new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), new Vector2(0, 0), color);

        // Côtés : pour chaque pixel opaque, une face sur chacun de ses 4 bords qui touche du vide (ou le bord de l'image).
        // Les 4 sommets lisent le CENTRE du pixel : le côté prend exactement sa couleur.
        for (int py = 0; py < height; py++)
        for (int px = 0; px < width; px++)
        {
            if (!Opaque(pixels, width, height, px, py)) continue;

            float left = x0 + px * pixel, right = left + pixel;
            float bottom = y0 + py * pixel, top = bottom + pixel;
            var uv = new Vector2((px + 0.5f) / width, (py + 0.5f) / height);

            if (!Opaque(pixels, width, height, px - 1, py))
                AddSide(data, new Vector3(left, bottom, -halfT), new Vector3(left, top, -halfT), new Vector3(left, top, halfT), new Vector3(left, bottom, halfT), Vector3.left, uv, color);
            if (!Opaque(pixels, width, height, px + 1, py))
                AddSide(data, new Vector3(right, bottom, -halfT), new Vector3(right, top, -halfT), new Vector3(right, top, halfT), new Vector3(right, bottom, halfT), Vector3.right, uv, color);
            if (!Opaque(pixels, width, height, px, py + 1))
                AddSide(data, new Vector3(left, top, -halfT), new Vector3(left, top, halfT), new Vector3(right, top, halfT), new Vector3(right, top, -halfT), Vector3.up, uv, color);
            if (!Opaque(pixels, width, height, px, py - 1))
                AddSide(data, new Vector3(left, bottom, -halfT), new Vector3(left, bottom, halfT), new Vector3(right, bottom, halfT), new Vector3(right, bottom, -halfT), Vector3.down, uv, color);
        }

        return data;
    }

    // Pixel opaque ? (hors de l'image : vide)
    static bool Opaque(Color32[] pixels, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return false;
        return pixels[y * width + x].a >= OpaqueAlpha;
    }

    static void AddSide(ItemMeshData d, Vector3 a, Vector3 b, Vector3 c, Vector3 e, Vector3 normal, Vector2 uv, Color32 color)
    {
        AddQuad(d, a, b, c, e, normal, uv, uv, uv, uv, color);
    }

    // Un quad dont la normale est tournée vers l'extérieur. Unity dessine la face dont les sommets tournent dans le
    // sens des aiguilles d'une montre : on inverse l'ordre si besoin, pour que cross(b - a, c - a) suive la normale.
    static void AddQuad(ItemMeshData d, Vector3 a, Vector3 b, Vector3 c, Vector3 e, Vector3 normal,
                        Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvE, Color32 color)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f)
        {
            Vector3 tp = b; b = e; e = tp;
            Vector2 tu = uvB; uvB = uvE; uvE = tu;
        }

        int start = d.vertices.Count;
        d.vertices.Add(a); d.vertices.Add(b); d.vertices.Add(c); d.vertices.Add(e);
        d.uvs.Add(uvA); d.uvs.Add(uvB); d.uvs.Add(uvC); d.uvs.Add(uvE);
        for (int i = 0; i < 4; i++)
        {
            d.normals.Add(normal);
            d.colors.Add(color);
        }
        d.triangles.Add(start); d.triangles.Add(start + 1); d.triangles.Add(start + 2);
        d.triangles.Add(start); d.triangles.Add(start + 2); d.triangles.Add(start + 3);
    }
}
