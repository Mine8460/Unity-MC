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
    int selected;
    bool dirty;
    float saveTimer;

    // Déclenché à chaque changement (contenu, sélection, ouverture)
    public event Action Changed;

    public int Selected => selected;
    public ItemStack HeldStack => held;
    public ItemStack GetSlot(int index) => slots[index];

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
            foreach (ItemStack s in startItems) Add(s.type, s.count);
            return;
        }

        Add(BlockType.Stone, 64);
        Add(BlockType.Dirt, 64);
        Add(BlockType.Log, 32);
        Add(BlockType.Glass, 32);
        Add(BlockType.Torch, 32);
        Add(BlockType.StoneSlab, 32);
        Add(BlockType.Anvil, 8);
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) SetOpen(!IsOpen);
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
        Save();
    }

    // ------------------------------------------------------------------
    // Contenu
    // ------------------------------------------------------------------

    // Ajoute des objets (barre d'accès d'abord). Renvoie la quantité qui n'a PAS pu être ajoutée.
    public int Add(BlockType type, int count)
    {
        if (type == BlockType.Air || count <= 0) return 0;

        // 1) complète les piles existantes
        for (int i = 0; i < Size && count > 0; i++)
        {
            if (slots[i].IsEmpty || slots[i].type != type || slots[i].count >= MaxStack) continue;

            int moved = Mathf.Min(count, MaxStack - slots[i].count);
            slots[i].count += moved;
            count -= moved;
        }

        // 2) puis les cases vides
        for (int i = 0; i < Size && count > 0; i++)
        {
            if (!slots[i].IsEmpty) continue;

            int moved = Mathf.Min(count, MaxStack);
            slots[i] = new ItemStack(type, moved);
            count -= moved;
        }

        Touch();
        return count;
    }

    // Y a-t-il de la place pour au moins un objet de ce type ?
    public bool CanAccept(BlockType type)
    {
        for (int i = 0; i < Size; i++)
        {
            if (slots[i].IsEmpty) return true;
            if (slots[i].type == type && slots[i].count < MaxStack) return true;
        }
        return false;
    }

    // L'objet de la case sélectionnée de la barre d'accès (false si elle est vide)
    public bool TryGetSelected(out BlockType type)
    {
        ItemStack stack = slots[selected];
        type = stack.type;
        return !stack.IsEmpty;
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
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            Save();
        }

        Changed?.Invoke();
    }

    // Clic sur une case de l'écran d'inventaire (gauche ou droit)
    public void ClickSlot(int i, bool right)
    {
        ItemStack slot = slots[i];

        if (!right)
        {
            if (held.IsEmpty)
            {
                held = slot;
                slots[i] = default;
            }
            else if (slot.IsEmpty)
            {
                slots[i] = held;
                held = default;
            }
            else if (slot.type == held.type)
            {
                // fusionne : le surplus reste dans la main
                int moved = Mathf.Min(held.count, MaxStack - slot.count);
                slot.count += moved;
                held.count -= moved;
                slots[i] = slot;
                if (held.count <= 0) held = default;
            }
            else
            {
                slots[i] = held;
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
                    held = new ItemStack(slot.type, take);
                    slot.count -= take;
                    slots[i] = slot.count > 0 ? slot : default;
                }
            }
            else if (slot.IsEmpty)
            {
                slots[i] = new ItemStack(held.type, 1);
                held.count--;
                if (held.count <= 0) held = default;
            }
            else if (slot.type == held.type && slot.count < MaxStack)
            {
                slot.count++;
                slots[i] = slot;
                held.count--;
                if (held.count <= 0) held = default;
            }
            else
            {
                slots[i] = held;
                held = slot;
            }
        }

        Touch();
    }

    // Lâche la pile tenue par la souris (clic en dehors du panneau) : tout, ou un seul objet
    public void DropHeld(bool onlyOne)
    {
        if (held.IsEmpty) return;

        int n = onlyOne ? 1 : held.count;
        DropFromPlayer(new ItemStack(held.type, n));

        held.count -= n;
        if (held.count <= 0) held = default;

        Touch();
    }

    // À la fermeture : la pile de la souris retourne dans l'inventaire (ou est lâchée si c'est plein)
    void ReturnHeldStack()
    {
        if (held.IsEmpty) return;

        int left = Add(held.type, held.count);
        if (left > 0) DropFromPlayer(new ItemStack(held.type, left));
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
        DropFromPlayer(new ItemStack(stack.type, n));
    }

    // Jette des objets devant le joueur (ils ne peuvent pas être ramassés tout de suite)
    void DropFromPlayer(ItemStack stack)
    {
        if (stack.IsEmpty || world == null) return;

        Transform eye = cameraTransform != null ? cameraTransform : transform;
        Vector3 origin = eye.position + eye.forward * 0.4f - Vector3.up * 0.3f;
        world.DropItem(stack.type, stack.count, origin, eye.forward * 4f + Vector3.up * 1.5f, 2f);
    }

    // ------------------------------------------------------------------
    // Sauvegarde
    // ------------------------------------------------------------------

    [Serializable]
    class SaveData
    {
        public int[] types;
        public int[] counts;
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
            var data = new SaveData { types = new int[Size], counts = new int[Size], selected = selected };
            for (int i = 0; i < Size; i++)
            {
                data.types[i] = (int)slots[i].type;
                data.counts[i] = slots[i].IsEmpty ? 0 : slots[i].count;
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
                slots[i] = data.counts[i] > 0
                    ? new ItemStack((BlockType)data.types[i], Mathf.Min(data.counts[i], MaxStack))
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
