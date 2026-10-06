using System;
using System.Collections.Generic;
using UnityEngine;

// Casse des blocs comme Minecraft :
//   - maintenir le clic gauche ; chaque bloc a son temps de casse (BlockInfo.breakTime), divisé par la vitesse
//     du bon outil ; sans l'outil exigé (pierre, minerais...), le bloc se casse plus lentement et ne lâche rien ;
//   - l'outil s'use d'un point par bloc cassé, et casse quand il est usé ;
//   - des fissures apparaissent sur le bloc, en 10 étapes, en épousant sa forme (dalle, enclume...) ;
//   - relâcher le clic ou viser un autre bloc remet la progression à zéro ;
//   - en maintenant le clic, une petite pause sépare deux blocs cassés (sauf les blocs instantanés).
// À mettre sur le joueur. Retire la casse instantanée de ton propre script de clic (la pose ne change pas).
public class BlockBreaker : MonoBehaviour
{
    [SerializeField] World world;
    [Tooltip("Caméra du joueur (vide = Camera.main)")]
    [SerializeField] Camera cam;
    [Tooltip("Distance max pour casser un bloc")]
    [SerializeField] float reach = 5f;
    [SerializeField] LayerMask hitLayers = ~0;
    [Tooltip("Pause entre deux blocs cassés en maintenant le clic (Minecraft : 0,25 s)")]
    [SerializeField] float breakCooldown = 0.25f;
    [Tooltip("Matériau des fissures (shader Voxel/BlockCrack). Vide = créé automatiquement")]
    [SerializeField] Material crackMaterial;

    GameObject overlay;
    Mesh overlayMesh;
    Texture2D[] crackTextures;

    Inventory inventory;

    Vector3Int target;
    BlockType targetType;
    ItemType targetTool;   // objet tenu au début de la casse (en changer la recommence)
    bool hasTarget;
    float progress;     // 0 à 1
    float cooldown;
    int shownStage = -1;

    // 0 à 1 : progression de la casse en cours (utile pour une barre, un son, une animation...)
    public float Progress => hasTarget ? progress : 0f;

    void Start()
    {
        if (world == null) world = FindFirst<World>();
        if (cam == null) cam = Camera.main;
        if (world != null) inventory = world.PlayerInventory; // sans inventaire : on casse à la main

        if (world == null || cam == null)
        {
            Debug.LogError("BlockBreaker : World ou caméra introuvable.", this);
            enabled = false;
            return;
        }

        if (crackMaterial == null)
        {
            Shader shader = Shader.Find("Voxel/BlockCrack");
            if (shader == null)
            {
                Debug.LogError("BlockBreaker : shader Voxel/BlockCrack introuvable (ajoute BlockCrack.shader au projet).", this);
                enabled = false;
                return;
            }
            crackMaterial = new Material(shader);
        }

        crackTextures = CrackTextures.Get();

        overlay = new GameObject("BlockCrack");
        overlayMesh = new Mesh { name = "BlockCrack" };
        overlay.AddComponent<MeshFilter>().sharedMesh = overlayMesh;
        var renderer = overlay.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = crackMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        overlay.SetActive(false);
    }

    void OnDestroy()
    {
        if (overlay != null) Destroy(overlay);
        if (overlayMesh != null) Destroy(overlayMesh);
    }

    void Update()
    {
        cooldown = Mathf.Max(0f, cooldown - Time.deltaTime);

        bool active = Cursor.lockState == CursorLockMode.Locked && !Inventory.IsOpen;
        if (!active || !Input.GetMouseButton(0) || !TryGetTarget(out Vector3Int block))
        {
            StopBreaking();
            return;
        }

        BlockType type = world.GetBlock(block.x, block.y, block.z);
        ItemStack held = inventory != null ? inventory.SelectedStack : default;
        float time = ItemDatabase.BreakTime(type, held, out bool canHarvest);
        if (type == BlockType.Air || time < 0f)
        {
            StopBreaking(); // incassable (bedrock)
            return;
        }

        // Nouveau bloc visé, bloc changé ou autre objet en main : on recommence à zéro
        ItemType tool = held.IsEmpty ? ItemType.None : held.type;
        if (!hasTarget || block != target || type != targetType || tool != targetTool)
        {
            StartBreaking(block, type);
            targetTool = tool;
        }

        if (cooldown > 0f) return;

        progress += time <= 0f ? 1f : Time.deltaTime / time;

        if (progress >= 1f)
        {
            world.BreakBlock(block.x, block.y, block.z, canHarvest); // sans le bon outil : rien n'est lâché
            if (time > 0f && inventory != null) inventory.DamageSelected(1); // l'outil s'use (pas sur un bloc instantané)
            StopBreaking();
            if (time > 0f) cooldown = breakCooldown; // les blocs instantanés (herbe haute) se fauchent sans pause
            return;
        }

        ShowCracks();
    }

    // Bloc visé : le rayon touche le mesh de visée d'un chunk ; le bloc est juste derrière le point touché
    bool TryGetTarget(out Vector3Int block)
    {
        block = default;
        var ray = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, reach, hitLayers, QueryTriggerInteraction.Ignore)) return false;
        if (hit.collider.GetComponent<Chunk>() == null) return false;

        block = Vector3Int.FloorToInt(hit.point - hit.normal * 0.01f);
        return true;
    }

    void StartBreaking(Vector3Int block, BlockType type)
    {
        target = block;
        targetType = type;
        hasTarget = true;
        progress = 0f;
        shownStage = -1;

        BuildOverlay(type);
        overlay.transform.position = block;
        overlay.SetActive(false);
    }

    void StopBreaking()
    {
        hasTarget = false;
        progress = 0f;
        shownStage = -1;
        if (overlay != null) overlay.SetActive(false);
    }

    void ShowCracks()
    {
        int stage = Mathf.Clamp((int)(progress * CrackTextures.Stages), 0, CrackTextures.Stages - 1);
        if (stage != shownStage)
        {
            shownStage = stage;
            crackMaterial.SetTexture("_MainTex", crackTextures[stage]);
        }
        overlay.SetActive(true);
    }

    // Surcouche des fissures : le mesh du bloc lui-même (il épouse donc sa forme), à peine agrandi,
    // avec des UV projetées face par face pour que la fissure couvre chaque face comme une texture.
    void BuildOverlay(BlockType type)
    {
        Mesh source = world.GetBlockMesh(type);
        Vector3[] v = source.vertices;
        Vector3[] n = source.normals;

        var verts = new Vector3[v.Length];
        var uvs = new Vector2[v.Length];
        var center = new Vector3(0.5f, 0.5f, 0.5f);

        for (int i = 0; i < v.Length; i++)
        {
            uvs[i] = PlanarUV(v[i], n[i]);
            verts[i] = center + (v[i] - center) * 1.002f + n[i] * 0.001f;
        }

        overlayMesh.Clear();
        overlayMesh.vertices = verts;
        overlayMesh.uv = uvs;
        overlayMesh.triangles = source.triangles;
        overlayMesh.RecalculateBounds();
    }

    // UV d'après la position dans le bloc, selon l'axe de la face (le dessus utilise x et z, les côtés la hauteur)
    static Vector2 PlanarUV(Vector3 p, Vector3 n)
    {
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
        if (ay >= ax && ay >= az) return new Vector2(p.x, p.z);
        if (ax >= az) return new Vector2(p.z, p.y);
        return new Vector2(p.x, p.y);
    }

    static T FindFirst<T>() where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType<T>();
#else
        return UnityEngine.Object.FindObjectOfType<T>();
#endif
    }
}

// Les 10 étapes de fissures. Si Assets/Resources/Cracks/destroy_stage_0.png ... destroy_stage_9.png existent
// (les noms de Minecraft), elles sont utilisées ; sinon elles sont générées : des fissures qui partent du
// centre et grandissent d'étape en étape (pixels noirs semi-transparents, avec un léger liseré).
public static class CrackTextures
{
    public const int Stages = 10;
    const int Size = 16;

    static Texture2D[] cache;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache() => cache = null;

    public static Texture2D[] Get()
    {
        if (cache != null) return cache;

        cache = new Texture2D[Stages];
        bool[][] generated = null;

        for (int s = 0; s < Stages; s++)
        {
            Texture2D custom = Resources.Load<Texture2D>("Cracks/destroy_stage_" + s);
            if (custom != null)
            {
                cache[s] = custom;
                continue;
            }

            if (generated == null) generated = Generate(Size, Stages, 1234);
            cache[s] = ToTexture(generated[s], Size);
        }
        return cache;
    }

    // Pour chaque étape, les pixels fissurés
    public static bool[][] Generate(int size, int stages, uint seed)
    {
        double[] order = CrackOrder(size, seed);

        // Ordre d'apparition des pixels ; chaque étape en révèle le même nombre de plus (rythme régulier)
        var pixels = new List<(double order, int index)>();
        for (int i = 0; i < order.Length; i++)
            if (order[i] >= 0) pixels.Add((order[i], i));
        pixels.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : a.index.CompareTo(b.index));

        var result = new bool[stages][];
        for (int s = 0; s < stages; s++)
        {
            int count = (int)Math.Round(pixels.Count * (s + 1) / (double)stages);
            result[s] = new bool[size * size];
            for (int k = 0; k < count; k++) result[s][pixels[k].index] = true;
        }
        return result;
    }

    // « Moment » où chaque pixel se fissure (-1 = jamais) : des branches qui partent du centre et serpentent
    static double[] CrackOrder(int size, uint seed)
    {
        var walker = new CrackWalker(size, seed);
        double c = size / 2.0 - 0.5;
        const int branches = 6;
        for (int b = 0; b < branches; b++)
        {
            double angle = b / (double)branches * 2 * Math.PI + (walker.Next() - 0.5) * 0.6;
            walker.Walk(c, c, angle, (int)(size * 0.68), b * 0.8);
        }
        return walker.order;
    }

    sealed class CrackWalker
    {
        public readonly double[] order;
        readonly int size;
        uint state;

        public CrackWalker(int size, uint seed)
        {
            this.size = size;
            state = seed == 0 ? 1u : seed;
            order = new double[size * size];
            for (int i = 0; i < order.Length; i++) order[i] = -1;
        }

        // Nombre pseudo-aléatoire entre 0 et 1 (xorshift : toujours les mêmes fissures)
        public double Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFF) / (double)0x1000000;
        }

        public void Walk(double x, double y, double angle, int length, double t0)
        {
            double t = t0;
            for (int i = 0; i < length; i++)
            {
                int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
                if (ix < 0 || ix >= size || iy < 0 || iy >= size) return;

                int index = iy * size + ix;
                if (order[index] < 0 || order[index] > t) order[index] = t;

                angle += (Next() - 0.5) * 1.1;   // la fissure serpente
                x += Math.Cos(angle);
                y += Math.Sin(angle);
                t += 1;

                if (Next() < 0.16 && length > 3)  // ramification
                {
                    double turn = 0.7 + Next() * 0.6;
                    double side = Next() < 0.5 ? 1 : -1;
                    Walk(x, y, angle + turn * side, length * 2 / 3, t + 2);
                }
            }
        }
    }

    static Texture2D ToTexture(bool[] crack, int size)
    {
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = y * size + x;
            if (crack[i])
            {
                pixels[i] = new Color32(0, 0, 0, 170);
                continue;
            }

            // Liseré : pixel voisin d'une fissure
            bool near = crack[y * size + (x + 1) % size] || crack[y * size + (x + size - 1) % size]
                     || crack[((y + 1) % size) * size + x] || crack[((y + size - 1) % size) * size + x];
            pixels[i] = near ? new Color32(0, 0, 0, 50) : new Color32(0, 0, 0, 0);
        }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}
