using UnityEngine;
using UnityEngine.UI;

// Santé, faim, chute, noyade, mort et réapparition, nourriture.
// À mettre sur le joueur (même objet que PlayerController). Tout se règle dans l'inspecteur.
[DisallowMultipleComponent]
public class PlayerStats : MonoBehaviour
{
    [Header("Références (auto-trouvées si vides)")]
    [SerializeField] PlayerController player;
    [SerializeField] Inventory inventory;

    [Header("Santé / faim (en demi-unités : 20 = 10 cœurs)")]
    [SerializeField] float maxHealth = 20f;
    [SerializeField] int maxFood = 20;

    [Header("Chute")]
    [Tooltip("Blocs de chute sans dégâts (Minecraft : 3). Dégâts = distance - ce seuil")]
    [SerializeField] float safeFall = 3f;

    [Header("Noyade")]
    [SerializeField] float maxAir = 15f;
    [SerializeField] float drownDamage = 2f;

    [Header("Faim")]
    [SerializeField] bool hungerEnabled = true;
    [SerializeField] float regenInterval = 4f;
    [SerializeField] float starveInterval = 4f;
    [Tooltip("Temps pour manger")]
    [SerializeField] float eatTime = 1.6f;

    [Header("Textures perso (optionnel : vide = icônes générées)")]
    [Tooltip("Cœur plein / moitié / vide (le vide sert aussi de fond). Fais-les en PNG carré, même taille.")]
    [SerializeField] Texture2D heartFullTex, heartHalfTex, heartEmptyTex;
    [SerializeField] Texture2D foodFullTex, foodHalfTex, foodEmptyTex;
    [SerializeField] Texture2D bubbleTex;

    [Header("Affichage")]
    [Tooltip("Décalage vertical au-dessus de la barre d'accès (unités d'interface)")]
    [SerializeField] float gapAboveHotbar = 4f;

    public float Health { get; private set; }
    public int Food { get; private set; }
    public bool IsDead { get; private set; }

    float saturation = 5f, exhaustion;
    float air, drownTimer, regenTimer, starveTimer;
    float invulnerableUntil, hurtFlash;
    float eatProgress;
    Vector3 lastPos;

    Texture2D heartFull, heartHalf, heartEmpty, foodFull, foodHalf, foodEmpty, bubble;

    void Awake()
    {
        if (player == null) player = GetComponent<PlayerController>();
        if (inventory == null) inventory = Find<Inventory>();
        Health = maxHealth;
        Food = maxFood;
        air = maxAir;
        lastPos = transform.position;
        BuildIcons();
    }

    void OnEnable()
    {
        if (player == null) player = GetComponent<PlayerController>();
        if (player == null) return;
        player.Landed += OnLanded;
        player.Jumped += OnJumped;
    }

    void OnDisable()
    {
        if (player == null) return;
        player.Landed -= OnLanded;
        player.Jumped -= OnJumped;
    }

    // ------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------

    public void Damage(float amount)
    {
        if (IsDead || amount <= 0f || Time.time < invulnerableUntil) return;
        Health = Mathf.Max(0f, Health - amount);
        invulnerableUntil = Time.time + 0.5f;
        hurtFlash = 0.35f;
        AddExhaustion(0.1f);
        if (Health <= 0f) Die();
    }

    public void Heal(float amount) { if (!IsDead) Health = Mathf.Min(maxHealth, Health + amount); }

    public void Eat(int food, float sat)
    {
        Food = Mathf.Min(maxFood, Food + food);
        saturation = Mathf.Min(Food, saturation + sat);
    }

    public void AddExhaustion(float amount) { if (hungerEnabled) exhaustion += amount; }

    void Die()
    {
        IsDead = true;
        eatProgress = 0f;
        if (player != null) player.InputLocked = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void DoRespawn()
    {
        IsDead = false;
        Health = maxHealth;
        Food = maxFood;
        saturation = 5f; exhaustion = 0f;
        air = maxAir;
        invulnerableUntil = Time.time + 2f; // petite protection à la réapparition
        if (player != null) { player.Respawn(); player.InputLocked = false; }
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        lastPos = transform.position;
    }

    // ------------------------------------------------------------------
    // Événements du joueur
    // ------------------------------------------------------------------

    void OnLanded(float fall)
    {
        float dmg = Mathf.Ceil(fall - safeFall);
        if (dmg > 0f) Damage(dmg);
    }

    void OnJumped() => AddExhaustion(player.IsSprinting ? 0.2f : 0.05f);

    // ------------------------------------------------------------------

    void Update()
    {
        RefreshUI();
        if (IsDead) return;

        float dt = Time.deltaTime;
        hurtFlash = Mathf.Max(0f, hurtFlash - dt);

        // Distance parcourue en courant
        Vector3 p = transform.position;
        float moved = new Vector2(p.x - lastPos.x, p.z - lastPos.z).magnitude;
        lastPos = p;
        if (moved < 2f && player.IsSprinting) AddExhaustion(moved * 0.1f);

        UpdateAir(dt);
        UpdateHunger(dt);
        UpdateEating(dt);
    }

    void UpdateAir(float dt)
    {
        if (player.EyeUnderwater)
        {
            air -= dt;
            if (air <= 0f)
            {
                air = 0f;
                drownTimer -= dt;
                if (drownTimer <= 0f) { drownTimer = 1f; invulnerableUntil = 0f; Damage(drownDamage); }
            }
        }
        else
        {
            air = Mathf.Min(maxAir, air + dt * 6f); // on reprend son souffle vite
            drownTimer = 0f;
        }
    }

    void UpdateHunger(float dt)
    {
        if (!hungerEnabled) return;

        if (exhaustion >= 4f)
        {
            exhaustion -= 4f;
            if (saturation > 0f) saturation = Mathf.Max(0f, saturation - 1f);
            else Food = Mathf.Max(0, Food - 1);
        }

        if (Food >= 18 && Health < maxHealth)
        {
            regenTimer += dt;
            if (regenTimer >= regenInterval * (Food >= 20 && saturation > 0f ? 0.2f : 1f))
            {
                regenTimer = 0f;
                Heal(1f);
                AddExhaustion(0.6f);
            }
        }
        else regenTimer = 0f;

        if (Food <= 0)
        {
            starveTimer += dt;
            if (starveTimer >= starveInterval)
            {
                starveTimer = 0f;
                if (Health > 1f) { invulnerableUntil = 0f; Damage(1f); }
            }
        }
        else starveTimer = 0f;
    }

    void UpdateEating(float dt)
    {
        bool can = inventory != null && !Inventory.IsOpen && Cursor.lockState == CursorLockMode.Locked
                   && Input.GetMouseButton(1) && Food < maxFood;
        ItemInfo info = default;
        if (can)
        {
            ItemStack s = inventory.SelectedStack;
            can = !s.IsEmpty;
            if (can) { info = ItemDatabase.Get(s.type); can = info.food > 0; }
        }

        if (!can) { eatProgress = 0f; return; }

        eatProgress += dt;
        if (eatProgress >= eatTime)
        {
            eatProgress = 0f;
            Eat(info.food, info.saturation);
            inventory.ConsumeSelected(1);
        }
    }

    // ------------------------------------------------------------------
    // Affichage
    // ------------------------------------------------------------------

    void OnGUI()
    {
        // Flash rouge quand on est blessé
        if (hurtFlash > 0f || IsDead)
        {
            Color prev = GUI.color;
            GUI.color = new Color(0.8f, 0f, 0f, IsDead ? 0.55f : hurtFlash * 1.2f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        if (IsDead) { DrawDeathScreen(); return; }

        float cx = Screen.width * 0.5f;

        // Barre de progression en mangeant
        if (eatProgress > 0f)
        {
            float w = 120f, h = 8f, bx = cx - w * 0.5f, by = Screen.height * 0.5f + 40f;
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(bx - 2, by - 2, w + 4, h + 4), Texture2D.whiteTexture);
            GUI.color = new Color(0.9f, 0.75f, 0.3f, 1f);
            GUI.DrawTexture(new Rect(bx, by, w * Mathf.Clamp01(eatProgress / eatTime), h), Texture2D.whiteTexture);
            GUI.color = prev;
        }
    }

    // ------------------------------------------------------------------
    // Cœurs, faim, bulles : de vraies images dans le Canvas de la barre d'accès.
    // Elles suivent donc sa taille et sa position (et restent nettes, sans flou).
    // ------------------------------------------------------------------

    const int IconCount = 10;
    RectTransform vitals;
    RawImage[] heartBg, heartFg, foodBg, foodFg, bubbleImg;
    InventoryUI inventoryUI;

    void BuildUI()
    {
        if (inventoryUI == null) inventoryUI = Find<InventoryUI>();
        if (inventoryUI == null || inventoryUI.HotbarRect == null) return; // pas encore construite

        RectTransform hotbar = inventoryUI.HotbarRect;
        float unit = hotbar.sizeDelta.x / 182f;   // comme Minecraft : barre de 182 px, icônes de 9 px espacées de 8
        float size = 9f * unit, step = 8f * unit;

        var go = new GameObject("Vitals", typeof(RectTransform));
        vitals = (RectTransform)go.transform;
        vitals.SetParent(hotbar, false);
        vitals.anchorMin = vitals.anchorMax = new Vector2(0.5f, 1f);
        vitals.pivot = new Vector2(0.5f, 0f);
        vitals.sizeDelta = new Vector2(hotbar.sizeDelta.x, size);
        vitals.anchoredPosition = new Vector2(0f, gapAboveHotbar);

        heartBg = new RawImage[IconCount]; heartFg = new RawImage[IconCount];
        foodBg = new RawImage[IconCount];  foodFg = new RawImage[IconCount];
        bubbleImg = new RawImage[IconCount];

        for (int i = 0; i < IconCount; i++)
        {
            // Cœurs : de gauche à droite. Faim et bulles : de droite à gauche.
            // (Le parent "Vitals" est centré : on se place par rapport à son bord gauche / droit.)
            float left = -vitals.sizeDelta.x * 0.5f + size * 0.5f + i * step;
            float right = vitals.sizeDelta.x * 0.5f - size * 0.5f - i * step;

            heartBg[i] = MakeCentered(left, 0f, size);
            heartFg[i] = MakeCentered(left, 0f, size);
            foodBg[i] = MakeCentered(right, 0f, size);
            foodFg[i] = MakeCentered(right, 0f, size);
            bubbleImg[i] = MakeCentered(right, size + 2f * unit, size);
        }
    }

    RawImage MakeCentered(float x, float y, float size)
    {
        var go = new GameObject("Icon", typeof(RectTransform), typeof(RawImage));
        var rt = (RectTransform)go.transform;
        rt.SetParent(vitals, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(size, size);
        rt.anchoredPosition = new Vector2(x, y);
        var img = go.GetComponent<RawImage>();
        img.raycastTarget = false;
        return img;
    }

    static void Show(RawImage img, Texture2D tex)
    {
        bool on = tex != null;
        if (img.enabled != on) img.enabled = on;
        if (on && img.texture != tex) img.texture = tex;
    }

    void RefreshUI()
    {
        if (vitals == null) { BuildUI(); if (vitals == null) return; }

        vitals.gameObject.SetActive(!IsDead);
        if (IsDead) return;

        for (int i = 0; i < IconCount; i++)
        {
            float hp = Health - i * 2f;
            Show(heartBg[i], heartEmpty);
            Show(heartFg[i], hp >= 2f ? heartFull : hp >= 1f ? heartHalf : null);

            int f = Food - i * 2;
            Show(foodBg[i], foodEmpty);
            Show(foodFg[i], f >= 2 ? foodFull : f >= 1 ? foodHalf : null);

            // Cœurs qui tremblent à moins de 2 cœurs
            float shake = Health <= 4f ? Mathf.Sin(Time.time * 40f + i) * 1.5f : 0f;
            heartBg[i].rectTransform.anchoredPosition = new Vector2(heartBg[i].rectTransform.anchoredPosition.x, shake);
            heartFg[i].rectTransform.anchoredPosition = heartBg[i].rectTransform.anchoredPosition;

            int bubbles = Mathf.CeilToInt(air / maxAir * 10f);
            Show(bubbleImg[i], air < maxAir - 0.01f && i < bubbles ? bubble : null);
        }
    }

    void DrawDeathScreen()
    {
        var title = new GUIStyle(GUI.skin.label) { fontSize = 40, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        title.normal.textColor = Color.white;
        GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 60f), "Vous êtes mort !", title);

        var button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
        if (GUI.Button(new Rect(Screen.width * 0.5f - 120f, Screen.height * 0.5f, 240f, 44f), "Réapparaître", button))
            DoRespawn();
    }

    // ------------------------------------------------------------------
    // Icônes en pixel art (générées, pas de fichier à fournir)
    // ------------------------------------------------------------------

    static readonly string[] HeartShape =
    {
        ".XX...XX.",
        "XXXX.XXXX",
        "XXXXXXXXX",
        "XXXXXXXXX",
        ".XXXXXXX.",
        "..XXXXX..",
        "...XXX...",
        "....X....",
        ".........",
    };

    static readonly string[] FoodShape =
    {
        "...XXXX..",
        "..XXXXXX.",
        ".XXXXXXX.",
        ".XXXXXXX.",
        "..XXXXXX.",
        "..XXXXX..",
        ".BBXXX...",
        "BB.......",
        "B........",
    };

    static readonly string[] BubbleShape =
    {
        "..XXXXX..",
        ".X.....X.",
        "X..XX...X",
        "X.......X",
        "X.......X",
        "X.......X",
        ".X.....X.",
        "..XXXXX..",
        ".........",
    };

    void BuildIcons()
    {
        var red = new Color32(220, 30, 30, 255);
        var dark = new Color32(40, 0, 0, 255);
        var brown = new Color32(190, 120, 50, 255);
        var darkBrown = new Color32(45, 28, 12, 255);

        heartFull = Make(HeartShape, red, dark, 0);
        heartHalf = Make(HeartShape, red, dark, 5);
        heartEmpty = Make(HeartShape, new Color32(0, 0, 0, 0), dark, 9, true);
        foodFull = Make(FoodShape, brown, darkBrown, 0);
        foodHalf = Make(FoodShape, brown, darkBrown, 5);
        foodEmpty = Make(FoodShape, new Color32(0, 0, 0, 0), darkBrown, 9, true);
        bubble = Make(BubbleShape, new Color32(150, 200, 255, 255), new Color32(40, 80, 140, 255), 0, false, true);

        // Tes textures remplacent celles générées
        if (heartFullTex != null) heartFull = heartFullTex;
        if (heartHalfTex != null) heartHalf = heartHalfTex;
        if (heartEmptyTex != null) heartEmpty = heartEmptyTex;
        if (foodFullTex != null) foodFull = foodFullTex;
        if (foodHalfTex != null) foodHalf = foodHalfTex;
        if (foodEmptyTex != null) foodEmpty = foodEmptyTex;
        if (bubbleTex != null) bubble = bubbleTex;

        // Pixels nets quoi qu'en dise l'import
        foreach (Texture2D t in new Texture2D[] { heartFull, heartHalf, heartEmpty, foodFull, foodHalf, foodEmpty, bubble })
            if (t != null) { t.filterMode = FilterMode.Point; t.wrapMode = TextureWrapMode.Clamp; }
    }

    // fillFrom : colonnes >= fillFrom restent vides (demi-icône) ; outlineOnly : juste le contour sombre
    static Texture2D Make(string[] shape, Color32 fill, Color32 outline, int fillFrom, bool outlineOnly = false, bool ring = false)
    {
        int n = shape.Length;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            char ch = shape[n - 1 - y][x];
            bool solid = ch == 'X' || ch == 'B';
            Color32 c = new Color32(0, 0, 0, 0);

            if (solid)
            {
                c = ch == 'B' ? new Color32(240, 235, 215, 255) : fill;
                if (outlineOnly || x >= fillFrom && fillFrom > 0) c = new Color32(0, 0, 0, 0);
                
            }
            else if (!ring)
            {
                // contour : case vide voisine d'une case pleine
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                for (int dx = -1; dx <= 1 && !near; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    char nc = shape[n - 1 - ny][nx];
                    if (nc == 'X' || nc == 'B') near = true;
                }
                if (near && (outlineOnly || true)) c = outline;
            }
            px[y * n + x] = c;
        }

        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    static T Find<T>() where T : Object
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindFirstObjectByType<T>();
#else
        return Object.FindObjectOfType<T>();
#endif
    }
}
