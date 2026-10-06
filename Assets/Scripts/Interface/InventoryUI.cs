using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

// Barre d'accès et écran d'inventaire, construits entièrement en code (aucun prefab ni Canvas à créer).
// Crée un GameObject vide avec ce script : l'inventaire et le monde sont trouvés automatiquement.
public class InventoryUI : MonoBehaviour
{
    [SerializeField] Inventory inventory;
    [SerializeField] World world;

    [Header("Apparence")]
    [SerializeField] float slotSize = 48f;
    [SerializeField] float gap = 4f;

    sealed class SlotView
    {
        public RectTransform rect;
        public GameObject frame;   // cadre blanc de la case sélectionnée
        public BlockIcon icon;
        public Text count;
        public GameObject durability;      // barre d'usure d'un outil (cachée s'il est neuf)
        public RectTransform durabilityFill;
        public Image durabilityImage;
    }

    readonly SlotView[] hudSlots = new SlotView[Inventory.HotbarSize];
    readonly SlotView[] panelSlots = new SlotView[Inventory.Size];
    readonly SlotView[] craftSlots = new SlotView[9];   // grille d'artisanat affichée en 3 x 3 (2 x 2 utilisées dans l'inventaire)
    SlotView resultView;
    Text craftTitle;

    RectTransform canvasRect, hud, panel;
    Canvas canvas;
    float lastScaleFactor = -1f;
    SlotView heldView;
    Text tooltip;
    Font font;
    Texture atlas;

    void Start()
    {
        if (inventory == null) inventory = FindFirst<Inventory>();
        if (world == null) world = FindFirst<World>();

        if (inventory == null || world == null)
        {
            Debug.LogError("InventoryUI : Inventory ou World introuvable dans la scène.", this);
            enabled = false;
            return;
        }

        atlas = world.AtlasTexture;
        font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" }, 16);

        BuildCanvas();
        BuildHud();
        BuildPanel();
        BuildHeldAndTooltip();

        inventory.Changed += Refresh;
        Refresh();
    }

    void OnDestroy()
    {
        if (inventory != null) inventory.Changed -= Refresh;
        if (canvasRect != null) Destroy(canvasRect.gameObject); // le Canvas n'est plus un enfant de ce GameObject
    }

    static T FindFirst<T>() where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType<T>();
#else
        return UnityEngine.Object.FindObjectOfType<T>();
#endif
    }

    // ------------------------------------------------------------------
    // Construction de l'interface
    // ------------------------------------------------------------------

    void BuildCanvas()
    {
        // Le Canvas doit être à la RACINE de la scène. S'il est enfant d'un autre Canvas (celui de ton viseur,
        // par exemple), il ne s'adapte plus à l'écran et la barre d'accès apparaît au milieu.
        var go = new GameObject("InventoryCanvas");

        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.matchWidthOrHeight = 0.5f;

        canvasRect = go.GetComponent<RectTransform>();
    }

    void BuildHud()
    {
        hud = NewRect("Hotbar", canvasRect);
        hud.anchorMin = hud.anchorMax = hud.pivot = new Vector2(0.5f, 0f);
        hud.sizeDelta = new Vector2(Inventory.HotbarSize * slotSize + (Inventory.HotbarSize - 1) * gap, slotSize);
        hud.anchoredPosition = new Vector2(0f, 16f);

        for (int i = 0; i < Inventory.HotbarSize; i++)
            hudSlots[i] = CreateSlot(hud, new Vector2(i * (slotSize + gap), 0f));
    }

    void BuildPanel()
    {
        const float pad = 16f, separation = 14f;
        float craftHeight = 3 * slotSize + 2 * gap + separation; // zone d'artisanat, au-dessus de l'inventaire

        panel = NewRect("Inventory", canvasRect);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(
            9 * slotSize + 8 * gap + 2 * pad,
            4 * slotSize + 3 * gap + separation + craftHeight + 2 * pad);
        panel.anchoredPosition = Vector2.zero;

        var background = panel.gameObject.AddComponent<Image>();
        background.color = new Color(0.05f, 0.05f, 0.05f, 0.9f);
        background.raycastTarget = false;

        // Artisanat : titre, grille (colonnes 2 à 4), flèche (colonne 5), résultat (colonne 6)
        craftTitle = CreateLabel(panel, new Vector2(pad, -pad), new Vector2(slotSize + gap, slotSize), 15, TextAnchor.UpperLeft);
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                craftSlots[row * 3 + col] = CreateSlot(panel, new Vector2(pad + (col + 1) * (slotSize + gap), -(pad + row * (slotSize + gap))));
        CreateLabel(panel, new Vector2(pad + 4 * (slotSize + gap), -(pad + (slotSize + gap))), new Vector2(slotSize, slotSize), 30, TextAnchor.MiddleCenter).text = "→";
        resultView = CreateSlot(panel, new Vector2(pad + 5 * (slotSize + gap) + gap, -(pad + (slotSize + gap))));

        // 3 rangées d'inventaire (cases 9 à 35)
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 9; col++)
            {
                Vector2 pos = new Vector2(pad + col * (slotSize + gap), -(pad + craftHeight + row * (slotSize + gap)));
                panelSlots[9 + row * 9 + col] = CreateSlot(panel, pos);
            }

        // la barre d'accès, en bas (cases 0 à 8)
        for (int col = 0; col < 9; col++)
        {
            Vector2 pos = new Vector2(pad + col * (slotSize + gap), -(pad + craftHeight + 3 * (slotSize + gap) + separation));
            panelSlots[col] = CreateSlot(panel, pos);
        }
    }

    Text CreateLabel(Transform parent, Vector2 anchoredPos, Vector2 size, int fontSize, TextAnchor alignment)
    {
        var rect = NewRect("Label", parent);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredPos;
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.color = new Color(0.85f, 0.85f, 0.85f);
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    void BuildHeldAndTooltip()
    {
        // La pile tenue par la souris, au-dessus de tout
        var heldRect = NewRect("Held", canvasRect);
        heldRect.anchorMin = heldRect.anchorMax = heldRect.pivot = new Vector2(0.5f, 0.5f);
        heldRect.sizeDelta = new Vector2(slotSize, slotSize);

        heldView = new SlotView { rect = heldRect };
        heldView.icon = CreateIcon(heldRect, 4f);
        CreateDurabilityBar(heldView, heldRect);
        heldView.count = CreateCount(heldRect);

        var tooltipRect = NewRect("Tooltip", canvasRect);
        tooltipRect.anchorMin = tooltipRect.anchorMax = new Vector2(0.5f, 0.5f);
        tooltipRect.pivot = new Vector2(0f, 1f);
        tooltipRect.sizeDelta = new Vector2(220f, 24f);

        tooltip = tooltipRect.gameObject.AddComponent<Text>();
        tooltip.font = font;
        tooltip.fontSize = 18;
        tooltip.color = Color.white;
        tooltip.alignment = TextAnchor.UpperLeft;
        tooltip.horizontalOverflow = HorizontalWrapMode.Overflow;
        tooltip.raycastTarget = false;
        tooltipRect.gameObject.AddComponent<Outline>();
    }

    SlotView CreateSlot(Transform parent, Vector2 anchoredPos)
    {
        var root = NewRect("Slot", parent);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0f, 1f);
        root.sizeDelta = new Vector2(slotSize, slotSize);
        root.anchoredPosition = anchoredPos;

        // Cadre de sélection : un peu plus grand que la case, derrière elle
        var frameRect = NewRect("Frame", root);
        Inset(frameRect, -3f);
        var frameImage = frameRect.gameObject.AddComponent<Image>();
        frameImage.color = Color.white;
        frameImage.raycastTarget = false;

        var backgroundRect = NewRect("Background", root);
        Inset(backgroundRect, 0f);
        var background = backgroundRect.gameObject.AddComponent<Image>();
        background.color = new Color(0.14f, 0.14f, 0.14f, 0.92f);
        background.raycastTarget = false;

        var view = new SlotView
        {
            rect = root,
            frame = frameRect.gameObject,
            icon = CreateIcon(root, 6f),
            count = CreateCount(root),
        };
        CreateDurabilityBar(view, root);
        return view;
    }

    // Barre d'usure sous l'icône, comme Minecraft : un fond noir et une jauge du vert au rouge
    static void CreateDurabilityBar(SlotView view, Transform parent)
    {
        var bar = NewRect("Durability", parent);
        bar.anchorMin = new Vector2(0f, 0f);
        bar.anchorMax = new Vector2(1f, 0f);
        bar.offsetMin = new Vector2(7f, 5f);
        bar.offsetMax = new Vector2(-7f, 8f);
        var background = bar.gameObject.AddComponent<Image>();
        background.color = Color.black;
        background.raycastTarget = false;

        var fill = NewRect("Fill", bar);
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = Vector2.one;
        fill.offsetMin = Vector2.zero;
        fill.offsetMax = new Vector2(0f, -1f);
        var fillImage = fill.gameObject.AddComponent<Image>();
        fillImage.raycastTarget = false;

        view.durability = bar.gameObject;
        view.durabilityFill = fill;
        view.durabilityImage = fillImage;
        bar.gameObject.SetActive(false);
    }

    BlockIcon CreateIcon(Transform parent, float padding)
    {
        var rect = NewRect("Icon", parent);
        Inset(rect, padding);

        var icon = rect.gameObject.AddComponent<BlockIcon>();
        icon.raycastTarget = false;
        icon.Setup(world, atlas);
        return icon;
    }

    Text CreateCount(Transform parent)
    {
        var rect = NewRect("Count", parent);
        Inset(rect, 3f);

        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = 16;
        text.alignment = TextAnchor.LowerRight;
        text.color = Color.white;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.raycastTarget = false;
        rect.gameObject.AddComponent<Shadow>();
        return text;
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go.GetComponent<RectTransform>();
    }

    // Remplit le parent, avec une marge (négative = déborde)
    static void Inset(RectTransform rect, float padding)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(padding, padding);
        rect.offsetMax = new Vector2(-padding, -padding);
    }

    // ------------------------------------------------------------------
    // Mise à jour
    // ------------------------------------------------------------------

    void Refresh()
    {
        bool open = Inventory.IsOpen;
        if (hud != null)
            hud.gameObject.SetActive(!open);
        if (panel != null)
            panel.gameObject.SetActive(open);

        for (int i = 0; i < Inventory.HotbarSize; i++)
            Fill(hudSlots[i], inventory.GetSlot(i), i == inventory.Selected);

        for (int i = 0; i < Inventory.Size; i++)
            Fill(panelSlots[i], inventory.GetSlot(i), i < Inventory.HotbarSize && i == inventory.Selected);

        // Artisanat : 2 x 2 dans l'inventaire, 3 x 3 à l'établi
        int size = inventory.CraftingSize;
        craftTitle.text = size == 3 ? "Établi" : "Artisanat";
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                SlotView view = craftSlots[row * 3 + col];
                bool used = row < size && col < size;
                view.rect.gameObject.SetActive(used);
                if (used) Fill(view, inventory.GetCraftSlot(row * size + col), false);
            }
        Fill(resultView, inventory.CraftResult, false);

        Fill(heldView, inventory.HeldStack, false);
        heldView.rect.gameObject.SetActive(open && !inventory.HeldStack.IsEmpty);
    }

    // Les textes sont générés à l'échelle de l'interface. Au lancement, le CanvasScaler ne l'a pas encore
    // calculée : sans ceci, les nombres restaient flous jusqu'à leur prochaine modification. Même chose si
    // la fenêtre change de taille. On les régénère donc dès que l'échelle change.
    void LateUpdate()
    {
        if (canvas == null || Mathf.Approximately(canvas.scaleFactor, lastScaleFactor)) return;
        lastScaleFactor = canvas.scaleFactor;

        foreach (Text text in canvasRect.GetComponentsInChildren<Text>(true)) // y compris l'inventaire fermé
            text.SetAllDirty();
    }

    static void Fill(SlotView view, ItemStack stack, bool selected)
    {
        if (view == null) return;
        view.icon.SetItem(stack.IsEmpty ? ItemType.None : stack.type);
        view.count.text = stack.count > 1 ? stack.count.ToString() : "";
        if (view.frame != null) view.frame.SetActive(selected);

        // Usure : seulement pour un outil déjà abîmé
        int durability = stack.IsEmpty ? 0 : ItemDatabase.Get(stack.type).durability;
        bool worn = durability > 0 && stack.damage > 0;
        if (view.durability == null) return;
        view.durability.SetActive(worn);
        if (worn)
        {
            float left = 1f - stack.damage / (float)durability;
            view.durabilityFill.anchorMax = new Vector2(left, 1f);
            view.durabilityImage.color = Color.Lerp(Color.red, Color.green, left);
        }
    }

    void Update()
    {
        if (!Inventory.IsOpen)
        {
            tooltip.gameObject.SetActive(false);
            return;
        }

        Vector2 mouse = Input.mousePosition;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, mouse, null, out Vector2 local);

        // La pile tenue suit la souris
        heldView.rect.anchoredPosition = local;

        // Case sous la souris
        int hovered = -1;
        for (int i = 0; i < Inventory.Size; i++)
        {
            if (RectTransformUtility.RectangleContainsScreenPoint(panelSlots[i].rect, mouse, null))
            {
                hovered = i;
                break;
            }
        }

        // Case d'artisanat ou résultat sous la souris
        int size = inventory.CraftingSize;
        int hoveredCraft = -1;
        for (int row = 0; row < size && hoveredCraft < 0; row++)
            for (int col = 0; col < size; col++)
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(craftSlots[row * 3 + col].rect, mouse, null))
                {
                    hoveredCraft = row * size + col;
                    break;
                }
            }
        bool hoveredResult = RectTransformUtility.RectangleContainsScreenPoint(resultView.rect, mouse, null);

        bool left = Input.GetMouseButtonDown(0);
        bool right = Input.GetMouseButtonDown(1);

        if (left || right)
        {
            if (hoveredResult)
                inventory.ClickResult(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)); // Maj : tout fabriquer
            else if (hoveredCraft >= 0)
                inventory.ClickCraftSlot(hoveredCraft, right);
            else if (hovered >= 0)
                inventory.ClickSlot(hovered, right);
            else if (!RectTransformUtility.RectangleContainsScreenPoint(panel, mouse, null))
                inventory.DropHeld(right); // clic en dehors du panneau : lâche la pile (un seul objet au clic droit)
        }

        // Info-bulle : le nom de l'objet sous la souris
        ItemStack hoveredStack = hovered >= 0 ? inventory.GetSlot(hovered)
                               : hoveredCraft >= 0 ? inventory.GetCraftSlot(hoveredCraft)
                               : hoveredResult ? inventory.CraftResult : default;
        bool showTooltip = !hoveredStack.IsEmpty && inventory.HeldStack.IsEmpty;
        tooltip.gameObject.SetActive(showTooltip);
        if (showTooltip)
        {
            ItemInfo info = ItemDatabase.Get(hoveredStack.type);
            tooltip.text = info.durability > 0
                ? $"{info.name} ({info.durability - hoveredStack.damage}/{info.durability})"
                : info.name;
            tooltip.rectTransform.anchoredPosition = local + new Vector2(16f, -16f);
        }
    }
}

// Icône d'un bloc dans l'inventaire, choisie comme dans Minecraft :
//   1. une image dessinée à la main, si elle existe : Assets/Resources/Icons/<NomDuBloc>.png (ex. Icons/Torch.png) ;
//   2. sinon, pour une plante (forme en croix), sa texture à plat ;
//   3. sinon, un rendu isométrique du VRAI mesh du bloc (cubes, dalle, enclume, modèles Blockbench...),
//      dessiné une seule fois dans une petite texture, puis mis en cache.
// L'affichage passe par RawImage, le composant standard d'Unity pour montrer une texture.
[RequireComponent(typeof(RawImage))]
public class BlockIcon : MonoBehaviour
{
    RawImage image;
    World world;
    Texture atlas;
    ItemType item;
    bool applied;

    public bool raycastTarget
    {
        set => Image.raycastTarget = value;
    }

    RawImage Image => image != null ? image : (image = GetComponent<RawImage>());

    public void Setup(World world, Texture atlas)
    {
        this.world = world;
        this.atlas = atlas;
        Apply();
    }

    public void SetBlock(BlockType block) => SetItem(ItemDatabase.FromBlock(block));

    public void SetItem(ItemType newItem)
    {
        if (applied && item == newItem) return;
        item = newItem;
        Apply();
    }

    void Apply()
    {
        applied = true;
        RawImage img = Image;

        if (item == ItemType.None || world == null)
        {
            img.enabled = false;
            return;
        }

        img.uvRect = new Rect(0f, 0f, 1f, 1f);

        // Un objet qui n'est pas un bloc (outil, lingot...) : son image, ou son icône générée
        if (!ItemDatabase.IsBlock(item))
        {
            img.texture = ItemIcons.Get(item);
            img.enabled = true;
            return;
        }

        BlockType type = ItemDatabase.ToBlock(item);

        // 1) Image dessinée à la main
        Texture2D custom = BlockIconCache.CustomIcon(type);
        if (custom != null)
        {
            img.texture = custom;
            img.enabled = true;
            return;
        }

        // 2) Plante : sa tuile de l'atlas, à plat
        BlockInfo info = BlockDatabase.Get(type);
        if (info.shape == BlockShape.Cross && atlas != null)
        {
            Vector2 min = BlockDatabase.TileUV(info.tileSide, 0f, 0f);
            Vector2 max = BlockDatabase.TileUV(info.tileSide, 1f, 1f);
            img.texture = atlas;
            img.uvRect = new Rect(min, max - min);
            img.enabled = true;
            return;
        }

        // 3) Rendu isométrique
        Texture2D iso = BlockIconCache.IsoIcon(type, world, atlas);
        img.texture = iso;
        img.enabled = iso != null;
    }
}

// Création et cache des icônes
public static class BlockIconCache
{
    const int IconSize = 64; // pixels de l'icône isométrique

    sealed class IconQuad
    {
        public readonly Vector2[] pos = new Vector2[4];   // 0..1 dans le carré de l'icône
        public readonly Vector2[] uv = new Vector2[4];
        public float shade;
    }

    static readonly Dictionary<BlockType, Texture2D> customIcons = new Dictionary<BlockType, Texture2D>();
    static readonly Dictionary<BlockType, Texture2D> isoIcons = new Dictionary<BlockType, Texture2D>();

    // Pixels de l'atlas, lus une seule fois
    static Color32[] atlasPixels;
    static int atlasWidth, atlasHeight;
    static Texture atlasSource;

    // Vide les caches à chaque lancement du jeu, même si le « Domain Reload » est désactivé dans les
    // options du projet (sinon une image ajoutée après une première partie ne serait jamais chargée)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCaches()
    {
        customIcons.Clear();
        isoIcons.Clear();
        atlasPixels = null;
        atlasSource = null;
    }

    public static Texture2D CustomIcon(BlockType t)
    {
        if (!customIcons.TryGetValue(t, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("Icons/" + t);
            customIcons[t] = tex;
        }
        return tex;
    }

    public static Texture2D IsoIcon(BlockType t, World world, Texture atlas)
    {
        if (isoIcons.TryGetValue(t, out Texture2D tex)) return tex;

        tex = RenderIso(BuildQuads(world.GetBlockMesh(t)), atlas);
        isoIcons[t] = tex;
        return tex;
    }

    // ------------------------------------------------------------------
    // Rendu isométrique, pixel par pixel
    // ------------------------------------------------------------------

    static Texture2D RenderIso(IconQuad[] quads, Texture atlas)
    {
        if (!ReadAtlas(atlas)) return null;

        var pixels = new Color32[IconSize * IconSize]; // transparent

        // Du plus loin au plus proche : les faces proches recouvrent les autres (algorithme du peintre)
        foreach (IconQuad q in quads)
        {
            // Un quad vu en projection orthographique est un parallélogramme : position et UV y sont affines
            Vector2 p0 = q.pos[0] * IconSize;
            Vector2 e1 = q.pos[1] * IconSize - p0;
            Vector2 e2 = q.pos[3] * IconSize - p0;
            float det = e1.x * e2.y - e1.y * e2.x;
            if (Mathf.Abs(det) < 1e-6f) continue; // vu par la tranche

            Vector2 uv0 = q.uv[0], du = q.uv[1] - q.uv[0], dv = q.uv[3] - q.uv[0];

            Vector2 min = Vector2.Min(Vector2.Min(p0, p0 + e1), Vector2.Min(p0 + e2, p0 + e1 + e2));
            Vector2 max = Vector2.Max(Vector2.Max(p0, p0 + e1), Vector2.Max(p0 + e2, p0 + e1 + e2));
            int x0 = Mathf.Max(0, Mathf.FloorToInt(min.x)), x1 = Mathf.Min(IconSize - 1, Mathf.CeilToInt(max.x));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(min.y)), y1 = Mathf.Min(IconSize - 1, Mathf.CeilToInt(max.y));

            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    float cx = px + 0.5f - p0.x, cy = py + 0.5f - p0.y;
                    float a = (cx * e2.y - cy * e2.x) / det;
                    float b = (e1.x * cy - e1.y * cx) / det;
                    if (a < 0f || a > 1f || b < 0f || b > 1f) continue;

                    Vector2 uv = uv0 + du * a + dv * b;
                    int tx = Mathf.Clamp((int)(uv.x * atlasWidth), 0, atlasWidth - 1);
                    int ty = Mathf.Clamp((int)(uv.y * atlasHeight), 0, atlasHeight - 1);
                    Color32 c = atlasPixels[ty * atlasWidth + tx];
                    if (c.a < 128) continue; // trous des feuilles, de la torche...

                    pixels[py * IconSize + px] = new Color32(
                        (byte)(c.r * q.shade), (byte)(c.g * q.shade), (byte)(c.b * q.shade), 255);
                }
        }

        var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    // Copie les pixels de l'atlas en mémoire (une fois). Marche même si la texture n'est pas « Read/Write » :
    // on la recopie alors dans une RenderTexture que l'on relit.
    static bool ReadAtlas(Texture atlas)
    {
        if (atlas == null) return false;
        if (atlasPixels != null && atlasSource == atlas) return true;

        atlasWidth = atlas.width;
        atlasHeight = atlas.height;

        if (atlas is Texture2D t2 && t2.isReadable)
        {
            atlasPixels = t2.GetPixels32();
        }
        else
        {
            RenderTexture rt = RenderTexture.GetTemporary(atlasWidth, atlasHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(atlas, rt);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, atlasWidth, atlasHeight), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            atlasPixels = copy.GetPixels32();
            Object.Destroy(copy);
        }

        atlasSource = atlas;
        return true;
    }

    // Les quads visibles du mesh (4 sommets consécutifs chacun), projetés, triés du plus loin au plus proche
    static IconQuad[] BuildQuads(Mesh mesh)
    {
        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Vector2[] uvs = mesh.uv;

        // Caméra isométrique placée en (+1, +1, -1) : elle voit le dessus, la face +X (à droite) et la face -Z (à gauche)
        Quaternion inverseView = Quaternion.Inverse(
            Quaternion.LookRotation(new Vector3(-1f, -1f, 1f).normalized, Vector3.up));
        var center = new Vector3(0.5f, 0.5f, 0.5f);

        var list = new List<(IconQuad quad, float depth)>();
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);

        for (int i = 0; i + 3 < verts.Length; i += 4)
        {
            // Face tournée vers la caméra ? (la caméra regarde le long de +Z)
            if ((inverseView * normals[i]).z >= -0.001f) continue;

            var q = new IconQuad { shade = ShadeOf(normals[i]) };
            float depth = 0f;

            for (int k = 0; k < 4; k++)
            {
                Vector3 p = inverseView * (verts[i + k] - center);
                q.pos[k] = new Vector2(p.x, p.y);
                q.uv[k] = uvs[i + k];
                depth += p.z * 0.25f;

                min = Vector2.Min(min, q.pos[k]);
                max = Vector2.Max(max, q.pos[k]);
            }

            list.Add((q, depth));
        }

        // Les plus lointains d'abord
        list.Sort((a, b) => b.depth.CompareTo(a.depth));

        // L'icône remplit le carré (92 %), centrée
        float size = Mathf.Max(max.x - min.x, max.y - min.y, 0.0001f);
        float scale = 0.92f / size;
        Vector2 offset = new Vector2(0.5f, 0.5f) - (min + max) * 0.5f * scale;

        var result = new IconQuad[list.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = list[i].quad;
            for (int k = 0; k < 4; k++)
                result[i].pos[k] = result[i].pos[k] * scale + offset;
        }
        return result;
    }

    // Luminosité par orientation, comme les icônes de Minecraft : dessus clair, gauche moyen, droite sombre
    static float ShadeOf(Vector3 n)
    {
        if (n.y > 0.5f) return 1f;
        if (n.z < -0.5f) return 0.8f;
        if (n.x > 0.5f) return 0.6f;
        return 1f;
    }
}
