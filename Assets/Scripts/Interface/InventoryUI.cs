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
    }

    readonly SlotView[] hudSlots = new SlotView[Inventory.HotbarSize];
    readonly SlotView[] panelSlots = new SlotView[Inventory.Size];

    RectTransform canvasRect, hud, panel;
    SlotView heldView;
    Text tooltip;
    Font font;
    [SerializeField] Texture atlas;

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

        var canvas = go.AddComponent<Canvas>();
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

        panel = NewRect("Inventory", canvasRect);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(
            9 * slotSize + 8 * gap + 2 * pad,
            4 * slotSize + 3 * gap + separation + 2 * pad);
        panel.anchoredPosition = Vector2.zero;

        var background = panel.gameObject.AddComponent<Image>();
        background.color = new Color(0.05f, 0.05f, 0.05f, 0.9f);
        background.raycastTarget = false;

        // 3 rangées d'inventaire (cases 9 à 35)
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 9; col++)
        {
            Vector2 pos = new Vector2(pad + col * (slotSize + gap), -(pad + row * (slotSize + gap)));
            panelSlots[9 + row * 9 + col] = CreateSlot(panel, pos);
        }

        // la barre d'accès, en bas (cases 0 à 8)
        for (int col = 0; col < 9; col++)
        {
            Vector2 pos = new Vector2(pad + col * (slotSize + gap), -(pad + 3 * (slotSize + gap) + separation));
            panelSlots[col] = CreateSlot(panel, pos);
        }
    }

    void BuildHeldAndTooltip()
    {
        // La pile tenue par la souris, au-dessus de tout
        var heldRect = NewRect("Held", canvasRect);
        heldRect.anchorMin = heldRect.anchorMax = heldRect.pivot = new Vector2(0.5f, 0.5f);
        heldRect.sizeDelta = new Vector2(slotSize, slotSize);

        heldView = new SlotView { rect = heldRect };
        heldView.icon = CreateIcon(heldRect, 4f);
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

        return new SlotView
        {
            rect = root,
            frame = frameRect.gameObject,
            icon = CreateIcon(root, 6f),
            count = CreateCount(root),
        };
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
        hud.gameObject.SetActive(!open);
        panel.gameObject.SetActive(open);

        for (int i = 0; i < Inventory.HotbarSize; i++)
            Fill(hudSlots[i], inventory.GetSlot(i), i == inventory.Selected);

        for (int i = 0; i < Inventory.Size; i++)
            Fill(panelSlots[i], inventory.GetSlot(i), i < Inventory.HotbarSize && i == inventory.Selected);

        Fill(heldView, inventory.HeldStack, false);
        heldView.rect.gameObject.SetActive(open && !inventory.HeldStack.IsEmpty);
    }

    static void Fill(SlotView view, ItemStack stack, bool selected)
    {
        view.icon.SetBlock(stack.IsEmpty ? BlockType.Air : stack.type);
        view.count.text = stack.count > 1 ? stack.count.ToString() : "";
        if (view.frame != null) view.frame.SetActive(selected);
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

        bool left = Input.GetMouseButtonDown(0);
        bool right = Input.GetMouseButtonDown(1);

        if (left || right)
        {
            if (hovered >= 0)
                inventory.ClickSlot(hovered, right);
            else if (!RectTransformUtility.RectangleContainsScreenPoint(panel, mouse, null))
                inventory.DropHeld(right); // clic en dehors du panneau : lâche la pile (un seul objet au clic droit)
        }

        // Info-bulle : le nom de l'objet sous la souris
        ItemStack hoveredStack = hovered >= 0 ? inventory.GetSlot(hovered) : default;
        bool showTooltip = !hoveredStack.IsEmpty && inventory.HeldStack.IsEmpty;
        tooltip.gameObject.SetActive(showTooltip);
        if (showTooltip)
        {
            tooltip.text = Regex.Replace(hoveredStack.type.ToString(), "(?<!^)([A-Z])", " $1");
            tooltip.rectTransform.anchoredPosition = local + new Vector2(16f, -16f);
        }
    }
}

// Icône d'un bloc dans l'inventaire, choisie comme dans Minecraft :
//   1. une image dessinée à la main, si elle existe : Assets/Resources/Icons/<NomDuBloc>.png (ex. Icons/Torch.png) ;
//   2. sinon, pour une plante (forme en croix), sa texture à plat ;
//   3. sinon, un rendu isométrique du VRAI mesh du bloc (cubes, dalle, enclume, modèles Blockbench...).
// Aucun bloc n'a donc besoin d'icône pour être affiché : on en ajoute seulement quand on veut.
public class BlockIcon : MaskableGraphic
{
    sealed class IconQuad
    {
        public readonly Vector2[] pos = new Vector2[4];   // 0..1 dans le carré de l'icône
        public readonly Vector2[] uv = new Vector2[4];
        public float shade;
    }

    static readonly Dictionary<BlockType, IconQuad[]> cache = new();

    // Icônes dessinées à la main, chargées une seule fois (null = pas d'image pour ce bloc)
    static readonly Dictionary<BlockType, Texture2D> customIcons = new();

    World world;
    Texture atlas;
    BlockType type;
    Texture2D customIcon;   // image du bloc affiché, ou null

    public override Texture mainTexture => customIcon != null ? customIcon : atlas;

    public void Setup(World world, Texture atlas)
    {
        this.world = world;
        this.atlas = atlas;
        SetMaterialDirty();
        SetVerticesDirty();
    }

    public void SetBlock(BlockType newType)
    {
        if (type == newType) return;
        type = newType;

        // Changer de texture (image <-> atlas) demande de refaire le matériau de l'icône
        Texture2D icon = type == BlockType.Air ? null : CustomIcon(type);
        if (icon != customIcon)
        {
            customIcon = icon;
            SetMaterialDirty();
        }

        SetVerticesDirty();
    }

    static Texture2D CustomIcon(BlockType t)
    {
        if (!customIcons.TryGetValue(t, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("Icons/" + t);
            customIcons[t] = tex;
        }
        return tex;
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (type == BlockType.Air || world == null) return;

        Rect r = GetPixelAdjustedRect();

        // 1) Image dessinée à la main
        if (customIcon != null)
        {
            AddFlat(vh, r, Vector2.zero, new Vector2(0f, 1f), Vector2.one, new Vector2(1f, 0f));
            return;
        }

        // 2) Plante : sa texture à plat (deux plans croisés vus en biais seraient illisibles)
        BlockInfo info = BlockDatabase.Get(type);
        if (info.shape == BlockShape.Cross)
        {
            int tile = info.tileSide;
            AddFlat(vh, r, BlockDatabase.TileUV(tile, 0f, 0f), BlockDatabase.TileUV(tile, 0f, 1f),
                           BlockDatabase.TileUV(tile, 1f, 1f), BlockDatabase.TileUV(tile, 1f, 0f));
            return;
        }

        // 3) Rendu isométrique du mesh
        IconQuad[] quads = GetQuads(type);

        foreach (IconQuad q in quads)
        {
            int start = vh.currentVertCount;
            var tint = new Color(color.r * q.shade, color.g * q.shade, color.b * q.shade, color.a);

            for (int k = 0; k < 4; k++)
            {
                UIVertex v = UIVertex.simpleVert;
                v.position = new Vector3(r.x + q.pos[k].x * r.width, r.y + q.pos[k].y * r.height, 0f);
                v.uv0 = q.uv[k];
                v.color = tint;
                vh.AddVert(v);
            }

            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }

    // Un carré qui remplit l'icône, avec les UV de ses 4 coins (bas-gauche, haut-gauche, haut-droite, bas-droite)
    void AddFlat(VertexHelper vh, Rect r, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
    {
        Vector2[] corners = { new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax), new Vector2(r.xMax, r.yMin) };
        Vector2[] uvs = { uv0, uv1, uv2, uv3 };

        for (int k = 0; k < 4; k++)
        {
            UIVertex v = UIVertex.simpleVert;
            v.position = new Vector3(corners[k].x, corners[k].y, 0f);
            v.uv0 = uvs[k];
            v.color = color;
            vh.AddVert(v);
        }

        vh.AddTriangle(0, 1, 2);
        vh.AddTriangle(0, 2, 3);
    }

    IconQuad[] GetQuads(BlockType t)
    {
        if (!cache.TryGetValue(t, out IconQuad[] quads))
        {
            quads = BuildQuads(world.GetBlockMesh(t));
            cache[t] = quads;
        }
        return quads;
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

        // Les plus lointains d'abord (algorithme du peintre)
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
