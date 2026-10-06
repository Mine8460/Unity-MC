using UnityEngine;

// Clic droit sur un établi : ouvre l'inventaire avec la grille d'artisanat 3 x 3.
// À mettre sur le joueur. Il s'exécute AVANT les autres scripts (DefaultExecutionOrder) : quand il ouvre l'établi,
// Inventory.IsOpen passe à true dans la même image, et ton script de pose (qui teste IsOpen) ne pose rien.
// Comme Minecraft : Maj + clic droit pose un bloc contre l'établi au lieu de l'ouvrir.
[DefaultExecutionOrder(-50)]
public class CraftingTableInteraction : MonoBehaviour
{
    [SerializeField] World world;
    [Tooltip("Caméra du joueur (vide = Camera.main)")]
    [SerializeField] Camera cam;
    [SerializeField] float reach = 5f;
    [SerializeField] LayerMask hitLayers = ~0;

    Inventory inventory;

    void Start()
    {
        if (world == null) world = FindFirst<World>();
        if (cam == null) cam = Camera.main;
        if (world != null) inventory = world.PlayerInventory;

        if (world == null || cam == null || inventory == null)
        {
            Debug.LogError("CraftingTableInteraction : World, caméra ou Inventory introuvable.", this);
            enabled = false;
        }
    }

    void Update()
    {
        if (Inventory.IsOpen || Cursor.lockState != CursorLockMode.Locked) return;
        if (!Input.GetMouseButtonDown(1)) return;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return; // Maj : on pose un bloc

        var ray = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, reach, hitLayers, QueryTriggerInteraction.Ignore)) return;
        if (hit.collider.GetComponent<Chunk>() == null) return;

        Vector3Int block = Vector3Int.FloorToInt(hit.point - hit.normal * 0.01f);
        if (world.GetBlock(block.x, block.y, block.z) == BlockType.CraftingTable)
            inventory.OpenCraftingTable();
    }

    static T FindFirst<T>() where T : Object
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindAnyObjectByType<T>();
#else
        return Object.FindObjectOfType<T>();
#endif
    }
}
