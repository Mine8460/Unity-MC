using System.Collections.Generic;
using UnityEngine;

// Charge tous les fichiers de monstres (Assets/Resources/Mobs/). Sans aucun fichier, un zombie intégré est utilisé.
public static class MobDatabase
{
    public const string ResourcesFolder = "Mobs";

    static List<MobDefinition> all;
    static Texture2D defaultSkin;

    public static IList<MobDefinition> All
    {
        get
        {
            if (all == null) Load();
            return all;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() { all = null; defaultSkin = null; }

    static void Load()
    {
        all = new List<MobDefinition>(Resources.LoadAll<MobDefinition>(ResourcesFolder));
        if (all.Count == 0)
        {
            MobDefinition z = ScriptableObject.CreateInstance<MobDefinition>();
            z.name = "Zombie";
            z.displayName = "Zombie";
            z.modelYawOffset = 0f;
            all.Add(z);
            Debug.Log("MobDatabase : aucun fichier dans Resources/" + ResourcesFolder + " : zombie intégré utilisé " +
                      "(menu Voxel > Créer le zombie d'exemple pour obtenir un fichier à modifier).");
        }
        else Debug.Log("MobDatabase : " + all.Count + " monstre(s) chargé(s).");
    }

    // Silhouette humaine, disposition de texture de Minecraft (image 64 x 32)
    public static MobPart[] HumanoidParts()
    {
        return new MobPart[]
        {
            new MobPart { name = "head", pivot = new Vector3(0, 24, 0), boxMin = new Vector3(-4, 0, -4), size = new Vector3(8, 8, 8),
                          uv = new Vector2Int(0, 0), anim = PartAnim.HeadLook },
            new MobPart { name = "body", pivot = new Vector3(0, 24, 0), boxMin = new Vector3(-4, -12, -2), size = new Vector3(8, 12, 4),
                          uv = new Vector2Int(16, 16) },
            new MobPart { name = "arm right", pivot = new Vector3(6, 22, 0), boxMin = new Vector3(-2, -10, -2), size = new Vector3(4, 12, 4),
                          uv = new Vector2Int(40, 16), anim = PartAnim.ArmsForwardA, animAmount = 45f },
            new MobPart { name = "arm left", pivot = new Vector3(-6, 22, 0), boxMin = new Vector3(-2, -10, -2), size = new Vector3(4, 12, 4),
                          uv = new Vector2Int(40, 16), mirror = true, anim = PartAnim.ArmsForwardB, animAmount = 45f },
            new MobPart { name = "leg right", pivot = new Vector3(2, 12, 0), boxMin = new Vector3(-2, -12, -2), size = new Vector3(4, 12, 4),
                          uv = new Vector2Int(0, 16), anim = PartAnim.SwingA, animAmount = 45f },
            new MobPart { name = "leg left", pivot = new Vector3(-2, 12, 0), boxMin = new Vector3(-2, -12, -2), size = new Vector3(4, 12, 4),
                          uv = new Vector2Int(0, 16), mirror = true, anim = PartAnim.SwingB, animAmount = 45f },
        };
    }

    public static Texture2D DefaultSkin()
    {
        if (defaultSkin == null) defaultSkin = PaintZombieSkin();
        return defaultSkin;
    }

    // Peau de zombie simple (64 x 32), même disposition que Minecraft : sert aussi de modèle à repeindre
    public static Texture2D PaintZombieSkin()
    {
        var t = new Texture2D(64, 32, TextureFormat.RGBA32, false) { name = "zombie", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Color32[] px = new Color32[64 * 32];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);

        System.Random rnd = new System.Random(7);
        Color32 skin = new Color32(90, 150, 75, 255), shirt = new Color32(50, 115, 150, 255), pants = new Color32(60, 55, 125, 255);
        Fill(px, 0, 0, 32, 16, skin, rnd);      // tête
        Fill(px, 16, 16, 24, 16, shirt, rnd);   // corps
        Fill(px, 40, 16, 16, 16, skin, rnd);    // bras
        Fill(px, 0, 16, 16, 16, pants, rnd);    // jambes

        // Visage (face avant de la tête : x 8..16, y 8..16)
        Color32 dark = new Color32(25, 35, 25, 255), eye = new Color32(10, 10, 10, 255);
        Set(px, 9, 11, eye); Set(px, 10, 11, eye); Set(px, 13, 11, eye); Set(px, 14, 11, eye);
        Set(px, 11, 13, dark); Set(px, 12, 13, dark);
        for (int x = 10; x <= 13; x++) Set(px, x, 14, dark);

        t.SetPixels32(px);
        t.Apply(false, false);
        return t;
    }

    static void Set(Color32[] px, int x, int y, Color32 c) { px[(31 - y) * 64 + x] = c; } // y compté depuis le haut

    static void Fill(Color32[] px, int x0, int y0, int w, int h, Color32 c, System.Random rnd)
    {
        for (int y = y0; y < y0 + h && y < 32; y++)
        for (int x = x0; x < x0 + w && x < 64; x++)
        {
            int n = rnd.Next(-9, 10);
            px[(31 - y) * 64 + x] = new Color32((byte)Mathf.Clamp(c.r + n, 0, 255), (byte)Mathf.Clamp(c.g + n, 0, 255), (byte)Mathf.Clamp(c.b + n, 0, 255), 255);
        }
    }
}
