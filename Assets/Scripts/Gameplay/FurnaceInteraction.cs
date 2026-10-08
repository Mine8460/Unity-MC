using UnityEngine;

// Clic droit sur un four : ouvre son écran (case à cuire, combustible, résultat).
// À mettre sur le joueur. Maj + clic droit : on pose un bloc contre le four au lieu de l'ouvrir.
[DefaultExecutionOrder(-50)]
public class FurnaceInteraction : MonoBehaviour
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
            Debug.LogError("FurnaceInteraction : World, caméra ou Inventory introuvable.", this);
            enabled = false;
        }
    }

    void Update()
    {
        if (Inventory.IsOpen || Cursor.lockState != CursorLockMode.Locked) return;
        if (!Input.GetMouseButtonDown(1)) return;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return;

        var ray = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, reach, hitLayers, QueryTriggerInteraction.Ignore)) return;
        if (hit.collider.GetComponent<Chunk>() == null) return;

        Vector3Int block = Vector3Int.FloorToInt(hit.point - hit.normal * 0.01f);
        if (FurnaceIds.IsFurnace(world.GetBlock(block.x, block.y, block.z)))
            inventory.OpenFurnace(block);
    }

    static T FindFirst<T>() where T : Object
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindFirstObjectByType<T>();
#else
        return Object.FindObjectOfType<T>();
#endif
    }
}
