using UnityEngine;

// Clic droit sur un levier, un bouton ou un répéteur : on l'utilise, même avec la main vide.
// À mettre sur le joueur (ou n'importe quel objet de la scène). Maj + clic droit : on ne l'utilise pas
// (pour poser un bloc contre lui).
[DisallowMultipleComponent]
public class BlockUse : MonoBehaviour
{
    [SerializeField] World world;
    [Tooltip("Vide = la caméra principale")]
    [SerializeField] Camera cam;
    [SerializeField] float reach = 5f;
    [SerializeField] LayerMask hitLayers = ~0;

    void Start()
    {
        if (world == null) world = Find<World>();
        if (cam == null) cam = GetComponentInChildren<Camera>();
        if (cam == null) cam = Camera.main;
    }

    void Update()
    {
        if (world == null || cam == null) return;
        if (!Input.GetMouseButtonDown(1)) return;
        if (Cursor.lockState != CursorLockMode.Locked || Inventory.IsOpen) return;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return;

        var ray = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, reach, hitLayers, QueryTriggerInteraction.Ignore)) return;
        if (hit.collider.GetComponent<Chunk>() == null) return;

        Vector3Int block = Vector3Int.FloorToInt(hit.point - hit.normal * 0.01f);
        world.TryInteract(block);
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
