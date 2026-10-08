using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Inventaire du joueur : 9 cases de barre d'accès + 27 cases d'inventaire, piles de 64 maximum.
// À mettre sur le GameObject du joueur.
public class Inventory : MonoBehaviour
{
    public const int HotbarSize = 9;
    public const int Size = 36;      // 0..8 = barre d'accès, 9..35 = inventaire
    public const int MaxStack = 64;

    // true quand l'écran d'inventaire est ouvert : ton code de casse/pose doit alors ne rien faire
    public static bool IsOpen { get; private set; }

    [Header("Références (trouvées automatiquement si vides)")]
    [SerializeField] World world;
    [SerializeField] Transform cameraTransform;

    [Header("Touches")]
    [SerializeField] KeyCode toggleKey = KeyCode.E;
    [SerializeField] KeyCode dropKey = KeyCode.G;   // Ctrl + touche : toute la pile

    [Header("Départ")]
    [Tooltip("Donne des objets de test au premier lancement (sans sauvegarde)")]
    [SerializeField] bool giveTestItems = true;
    [Tooltip("Vide = une sélection par défaut")]
    [SerializeField] List<ItemStack> startItems = new();

    [Header("Sauvegarde")]
    [SerializeField] bool saveToDisk = true;
    [SerializeField] string saveFileName = "inventory.json";

    readonly ItemStack[] slots = new ItemStack[Size];
    ItemStack held;     // la pile tenue par la souris (écran d'inventaire)

    // Grille d'artisanat : 2 x 2 dans l'inventaire, 3 x 3 à l'établi (rangée par rangée, de haut en bas)
    readonly ItemStack[] craftGrid = new ItemStack[9];
    int selected;
    bool dirty;
    float saveTimer;

    // Déclenché à chaque changement (contenu, sélection, ouverture)
    public event Action Changed;

    public int Selected => selected;
    public ItemStack HeldStack => held;
    public ItemStack GetSlot(int index) => slots[index];

    // Artisanat : taille de la grille (2 = inventaire, 3 = établi), ses cases, et l'objet qu'elle fabrique
    public int CraftingSize { get; private set; } = 2;
    public ItemStack GetCraftSlot(int index) => craftGrid[index];
    public ItemStack CraftResult => Crafting.Match(craftGrid, CraftingSize);

    // Four : quand l'écran est celui d'un four, FurnaceMode est vrai et Furnace donne son contenu
    public bool FurnaceMode { get; private set; }
    public Vector3Int FurnacePos { get; private set; }
    public FurnaceData Furnace => FurnaceMode && world != null ? world.GetFurnace(FurnacePos, true) : null;

    // ------------------------------------------------------------------
    // Cycle de vie
    // ------------------------------------------------------------------

    void Awake()
    {
        IsOpen = false;

        if (world == null) world = FindFirst<World>();
        if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;

        if (!Load()) GiveStartItems();
    }

    static T FindFirst<T>() where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType<T>();
#else
        return UnityEngine.Object.FindObjectOfType<T>();
#endif
    }

    void GiveStartItems()
    {
        if (!giveTestItems) return;

        if (startItems != null && startItems.Count > 0)
        {
            foreach (ItemStack s in startItems) Add(s.type, s.count, s.damage);
            return;
        }

        Add(BlockType.Stone, 64);
        Add(BlockType.Dirt, 64);
        Add(BlockType.Log, 32);
        Add(BlockType.Glass, 32);
        Add(BlockType.TintedGlass, 32);
        Add(BlockType.Anvil, 64);
        Add(BlockType.Sand, 16);

        // Outils, en attendant l'artisanat
        Add(ItemType.DiamondPickaxe, 1);
        Add(ItemType.DiamondAxe, 1);
        Add(ItemType.DiamondShovel, 1);

        // Redstone, pour tester
        Add(ItemType.RedstoneDust, 64);
        Add(ItemType.Repeater, 8);
        Add(ItemType.Apple, 8);
        Add(BlockType.Furnace, 2);
        Add(BlockType.IronOre, 16);
        Add(BlockType.GoldOre, 8);
        Add(BlockType.Sand, 16);
        Add(ItemType.Coal, 16);
        Add((BlockType)Redstone.LeverOff, 8);
        Add((BlockType)Redstone.ButtonOff, 8);
        Add((BlockType)Redstone.TorchLit, 16);
        Add((BlockType)Redstone.Lamp, 16);
        Add((BlockType)Redstone.Block, 16);
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (!IsOpen) CraftingSize = 2; // E : l'inventaire, avec la grille 2 x 2
            SetOpen(!IsOpen);
        }
        else if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) SetOpen(false);

        // Sauvegarde automatique toutes les 30 secondes s'il y a eu un changement
        saveTimer += Time.unscaledDeltaTime;
        if (dirty && saveTimer > 30f)
        {
            saveTimer = 0f;
            Save();
        }

        if (IsOpen) return;
        if (Cursor.lockState != CursorLockMode.Locked) return; // souris libre : on n'agit pas sur le jeu

        // Barre d'accès : touches 1 à 9 et molette
        for (int i = 0; i < HotbarSize; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) Select(i);
        }

        float wheel = Input.mouseScrollDelta.y;
        if (wheel > 0f) Select(selected - 1);
        else if (wheel < 0f) Select(selected + 1);

        if (Input.GetKeyDown(dropKey)) DropSelected(Input.GetKey(KeyCode.LeftControl));
    }

    void OnApplicationQuit()
    {
        ReturnHeldStack();
        ReturnCraftGrid();
        Save();
    }

    // ------------------------------------------------------------------
    // Contenu
    // ------------------------------------------------------------------

    // Ajoute des objets (barre d'accès d'abord). Renvoie la quantité qui n'a PAS pu être ajoutée.
    public int Add(ItemType type, int count, int damage = 0)
    {
        if (type == ItemType.None || count <= 0) return 0;

        var item = new ItemStack(type, 1, damage);
        int max = ItemDatabase.MaxStack(type);

        // 1) complète les piles existantes
        for (int i = 0; i < Size && count > 0; i++)
        {
            if (slots[i].IsEmpty || !slots[i].CanStackWith(item) || slots[i].count >= max) continue;

            int moved = Mathf.Min(count, max - slots[i].count);
            slots[i].count += moved;
            count -= moved;
        }

        // 2) puis les cases vides
        for (int i = 0; i < Size && count > 0; i++)
        {
            if (!slots[i].IsEmpty) continue;

            int moved = Mathf.Min(count, max);
            slots[i] = new ItemStack(type, moved, damage);
            count -= moved;
        }

        Touch();
        return count;
    }

    public int Add(BlockType block, int count) => Add(ItemDatabase.FromBlock(block), count);

    // Ajoute une pile (en gardant l'usure d'un outil). Renvoie la quantité qui n'a pas pu être ajoutée.
    public int Add(ItemStack stack) => stack.IsEmpty ? 0 : Add(stack.type, stack.count, stack.damage);

    // Y a-t-il de la place pour au moins un objet de ce type ?
    public bool CanAccept(ItemStack stack)
    {
        int max = ItemDatabase.MaxStack(stack.type);
        for (int i = 0; i < Size; i++)
        {
            if (slots[i].IsEmpty) return true;
            if (slots[i].CanStackWith(stack) && slots[i].count < max) return true;
        }
        return false;
    }

    public bool CanAccept(BlockType block) => CanAccept(new ItemStack(block, 1));

    // L'objet de la case sélectionnée de la barre d'accès (vide si rien)
    public ItemStack SelectedStack => slots[selected];

    // Le BLOC de la case sélectionnée (false si elle est vide ou si ce n'est pas un bloc : outil, lingot...)
    // Pour la poussière de redstone et le répéteur (des objets qui ne sont pas des blocs), c'est le bloc qu'ils posent.
    public bool TryGetSelected(out BlockType type)
    {
        ItemStack stack = slots[selected];
        type = stack.IsEmpty ? BlockType.Air : ItemDatabase.PlacedBlock(stack.type);
        return type != BlockType.Air;
    }

    // Abîme l'outil sélectionné ; il casse quand son usure atteint sa durabilité
    public void DamageSelected(int amount = 1)
    {
        ItemStack stack = slots[selected];
        int durability = ItemDatabase.Get(stack.type).durability;
        if (stack.IsEmpty || durability <= 0) return;

        stack.damage += amount;
        slots[selected] = stack.damage >= durability ? default : stack;
        Touch();
    }

    // Retire des objets de la case sélectionnée (après avoir posé un bloc, par exemple)
    public void ConsumeSelected(int amount = 1)
    {
        if (slots[selected].IsEmpty) return;

        slots[selected].count -= amount;
        if (slots[selected].count <= 0) slots[selected] = default;

        Touch();
    }

    public void Select(int index)
    {
        selected = ((index % HotbarSize) + HotbarSize) % HotbarSize;
        Touch();
    }

    // ------------------------------------------------------------------
    // Écran d'inventaire
    // ------------------------------------------------------------------

    public void SetOpen(bool open)
    {
        if (IsOpen == open) return;
        IsOpen = open;

        if (open)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            ReturnHeldStack();
            ReturnCraftGrid();
            FurnaceMode = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            Save();
        }

        Changed?.Invoke();
    }

    // Clic sur une case de l'écran d'inventaire (gauche ou droit)
    public void ClickSlot(int i, bool right)
    {
        ClickStack(ref slots[i], right);
        Touch();
    }

    // Clic sur une case de la grille d'artisanat : comme une case d'inventaire
    public void ClickCraftSlot(int i, bool right)
    {
        if (i < 0 || i >= CraftingSize * CraftingSize) return;
        ClickStack(ref craftGrid[i], right);
        Touch();
    }

    // Échange entre une case et la pile tenue par la souris, comme Minecraft :
    // clic gauche = prendre / poser / fusionner / échanger ; clic droit = prendre la moitié / poser un seul objet
    void ClickStack(ref ItemStack target, bool right)
    {
        ItemStack slot = target;

        if (!right)
        {
            if (held.IsEmpty)
            {
                held = slot;
                target = default;
            }
            else if (slot.IsEmpty)
            {
                target = held;
                held = default;
            }
            else if (slot.CanStackWith(held))
            {
                // fusionne : le surplus reste dans la main
                int moved = Mathf.Max(0, Mathf.Min(held.count, ItemDatabase.MaxStack(slot.type) - slot.count));
                slot.count += moved;
                held.count -= moved;
                target = slot;
                if (held.count <= 0) held = default;
            }
            else
            {
                target = held;
                held = slot;
            }
        }
        else
        {
            if (held.IsEmpty)
            {
                // prend la moitié (arrondie au-dessus)
                if (!slot.IsEmpty)
                {
                    int take = (slot.count + 1) / 2;
                    held = new ItemStack(slot.type, take, slot.damage);
                    slot.count -= take;
                    target = slot.count > 0 ? slot : default;
                }
            }
            else if (slot.IsEmpty)
            {
                target = new ItemStack(held.type, 1, held.damage);
                held.count--;
                if (held.count <= 0) held = default;
            }
            else if (slot.CanStackWith(held) && slot.count < ItemDatabase.MaxStack(slot.type))
            {
                slot.count++;
                target = slot;
                held.count--;
                if (held.count <= 0) held = default;
            }
            else
            {
                target = held;
                held = slot;
            }
        }
    }

    // Clic sur la case de résultat : prend UN exemplaire fabriqué (dans la main), ou, avec Maj,
    // en fabrique autant que possible directement dans l'inventaire
    public void ClickResult(bool all)
    {
        ItemStack result = CraftResult;
        if (result.IsEmpty) return;

        if (all)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                result = CraftResult;
                if (result.IsEmpty || !CanAccept(result)) break;
                Add(result);
                ConsumeCraftIngredients();
            }
        }
        else
        {
            if (held.IsEmpty) held = result;
            else if (held.CanStackWith(result) && held.count + result.count <= ItemDatabase.MaxStack(held.type)) held.count += result.count;
            else return; // la main tient autre chose
            ConsumeCraftIngredients();
        }

        Touch();
    }

    // Une fabrication consomme un objet de chaque case occupée de la grille
    void ConsumeCraftIngredients()
    {
        int n = CraftingSize * CraftingSize;
        for (int i = 0; i < n; i++)
        {
            if (craftGrid[i].IsEmpty) continue;
            craftGrid[i].count--;
            if (craftGrid[i].count <= 0) craftGrid[i] = default;
        }
    }

    // Ouvre l'inventaire avec la grille 3 x 3 de l'établi
    // Ouvre l'écran du four situé en pos
    public void OpenFurnace(Vector3Int pos)
    {
        if (IsOpen || world == null) return;
        if (world.GetFurnace(pos, true) == null) return;
        CraftingSize = 2;
        FurnacePos = pos;
        FurnaceMode = true;
        SetOpen(true);
    }

    // Clic sur une case du four : 0 = objet à cuire, 1 = combustible, 2 = résultat (on ne peut que le prendre)
    public void ClickFurnaceSlot(int part, bool right)
    {
        FurnaceData f = Furnace;
        if (f == null) return;

        if (part == 2)
        {
            if (f.output.IsEmpty) return;
            if (held.IsEmpty)
            {
                held = f.output;
                f.output = default;
            }
            else if (held.CanStackWith(f.output) && held.count + f.output.count <= ItemDatabase.MaxStack(held.type))
            {
                held.count += f.output.count;
                f.output = default;
            }
            Touch();
            return;
        }

        if (part == 1 && !held.IsEmpty && !Smelting.IsFuel(held.type)) return; // seulement des combustibles

        if (part == 0) ClickStack(ref f.input, right);
        else ClickStack(ref f.fuel, right);
        Touch();
    }

    public void OpenCraftingTable()
    {
        if (IsOpen) return;
        FurnaceMode = false;
        CraftingSize = 3;
        SetOpen(true);
    }

    // À la fermeture : ce qui reste dans la grille retourne dans l'inventaire (ou est lâché si c'est plein)
    void ReturnCraftGrid()
    {
        for (int i = 0; i < craftGrid.Length; i++)
        {
            if (craftGrid[i].IsEmpty) continue;
            int left = Add(craftGrid[i]);
            if (left > 0) DropFromPlayer(new ItemStack(craftGrid[i].type, left, craftGrid[i].damage));
            craftGrid[i] = default;
        }
    }

    // Lâche la pile tenue par la souris (clic en dehors du panneau) : tout, ou un seul objet
    public void DropHeld(bool onlyOne)
    {
        if (held.IsEmpty) return;

        int n = onlyOne ? 1 : held.count;
        DropFromPlayer(new ItemStack(held.type, n, held.damage));

        held.count -= n;
        if (held.count <= 0) held = default;

        Touch();
    }

    // À la fermeture : la pile de la souris retourne dans l'inventaire (ou est lâchée si c'est plein)
    void ReturnHeldStack()
    {
        if (held.IsEmpty) return;

        int left = Add(held);
        if (left > 0) DropFromPlayer(new ItemStack(held.type, left, held.damage));
        held = default;
    }

    // ------------------------------------------------------------------
    // Lâcher des objets
    // ------------------------------------------------------------------

    void DropSelected(bool wholeStack)
    {
        ItemStack stack = slots[selected];
        if (stack.IsEmpty) return;

        int n = wholeStack ? stack.count : 1;
        ConsumeSelected(n);
        DropFromPlayer(new ItemStack(stack.type, n, stack.damage));
    }

    // Jette des objets devant le joueur (ils ne peuvent pas être ramassés tout de suite)
    void DropFromPlayer(ItemStack stack)
    {
        if (stack.IsEmpty || world == null) return;

        Transform eye = cameraTransform != null ? cameraTransform : transform;
        Vector3 origin = eye.position + eye.forward * 0.4f - Vector3.up * 0.3f;
        world.DropItem(stack, origin, eye.forward * 4f + Vector3.up * 1.5f, 2f);
    }

    // ------------------------------------------------------------------
    // Sauvegarde
    // ------------------------------------------------------------------

    [Serializable]
    class SaveData
    {
        public int[] types;
        public int[] counts;
        public int[] damages;   // usure des outils (absente des anciennes sauvegardes)
        public int selected;
    }

    string SavePath => Path.Combine(Application.persistentDataPath, saveFileName);

    void Touch()
    {
        dirty = true;
        Changed?.Invoke();
    }

    public void Save()
    {
        if (!saveToDisk) return;

        try
        {
            var data = new SaveData { types = new int[Size], counts = new int[Size], damages = new int[Size], selected = selected };
            for (int i = 0; i < Size; i++)
            {
                data.types[i] = (int)slots[i].type;
                data.counts[i] = slots[i].IsEmpty ? 0 : slots[i].count;
                data.damages[i] = slots[i].damage;
            }

            File.WriteAllText(SavePath, JsonUtility.ToJson(data));
            dirty = false;
        }
        catch (Exception e)
        {
            Debug.LogError($"Inventory : échec de la sauvegarde ({e.Message})", this);
        }
    }

    bool Load()
    {
        if (!saveToDisk || !File.Exists(SavePath)) return false;

        try
        {
            var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
            if (data == null || data.types == null || data.counts == null ||
                data.types.Length != Size || data.counts.Length != Size)
                return false;

            for (int i = 0; i < Size; i++)
            {
                var type = (ItemType)data.types[i];
                int damage = data.damages != null && data.damages.Length == Size ? data.damages[i] : 0;
                slots[i] = data.counts[i] > 0
                    ? new ItemStack(type, Mathf.Min(data.counts[i], ItemDatabase.MaxStack(type)), damage)
                    : default;
            }

            selected = Mathf.Clamp(data.selected, 0, HotbarSize - 1);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Inventory : sauvegarde illisible, elle est ignorée ({e.Message})", this);
            return false;
        }
    }

    // Clic droit sur le composant dans l'Inspector : repart d'un inventaire neuf (au prochain lancement)
    [ContextMenu("Supprimer la sauvegarde de l'inventaire")]
    void DeleteSave()
    {
        if (File.Exists(SavePath)) File.Delete(SavePath);
        Debug.Log($"Inventory : sauvegarde supprimée ({SavePath})");
    }
}
