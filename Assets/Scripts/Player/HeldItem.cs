using UnityEngine;

// Objet tenu en main, en vue à la première personne (cube pour un bloc, objet extrudé en 3D pour le reste).
// À mettre sur le joueur. Il trouve tout seul la caméra, l'inventaire et le monde.
// Tous les réglages sont dans l'inspecteur : lance le jeu et bouge-les en direct pour placer l'objet à ton goût.
[DisallowMultipleComponent]
public class HeldItem : MonoBehaviour
{
    [Header("Références (auto-trouvées si vides)")]
    [SerializeField] World world;
    [SerializeField] Inventory inventory;
    [SerializeField] Transform cameraTransform;
    [SerializeField] PlayerController player;

    [Header("Bloc tenu")]
    [SerializeField] Vector3 blockPosition = new Vector3(0.5f, -0.42f, 0.8f);
    [SerializeField] Vector3 blockRotation = new Vector3(8f, -35f, 0f);
    [SerializeField] float blockSize = 0.38f;

    [Header("Objet tenu (charbon, nourriture, lingot...)")]
    [SerializeField] Vector3 itemPosition = new Vector3(0.48f, -0.32f, 0.8f);
    [Tooltip("Z = 45 redresse la diagonale de l'icône (manche en bas, tête en haut) ; Y > 0 tourne la face vers le centre de l'écran")]
    [SerializeField] Vector3 itemRotation = new Vector3(-5f, 30f, 45f);
    [SerializeField] float itemSize = 0.6f;

    [Header("Outil tenu (pioche, hache, pelle) : réglages à part")]
    [SerializeField] Vector3 toolPosition = new Vector3(0.48f, -0.32f, 0.8f);
    [SerializeField] Vector3 toolRotation = new Vector3(-5f, 30f, 45f);
    [SerializeField] float toolSize = 0.6f;

    [Header("Animations")]
    [SerializeField] float swingDuration = 0.32f;
    [Tooltip("Durée du changement d'objet (descend puis remonte)")]
    [SerializeField] float equipDuration = 0.25f;
    [SerializeField] float bobAmount = 0.015f;
    [SerializeField] float bobSpeed = 9f;
    [SerializeField] bool hideWhenInventoryOpen = true;

    Transform pivot;
    GameObject model;
    ItemStack shown;       // ce qui est actuellement affiché
    bool hasShown;
    bool shownIsTool;      // l'objet affiché est un outil (réglages à part)
    int shownSlot = -1;

    float swingTime = -1f; // < 0 : pas de coup en cours
    float equipTime = -1f; // < 0 : pas de changement en cours
    bool equipSwapped;
    float bobPhase;
    Vector3 basePos;
    Quaternion baseRot;

    void Start()
    {
        if (player == null) player = GetComponent<PlayerController>();
        if (world == null) world = Find<World>();
        if (inventory == null) inventory = Find<Inventory>();
        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>();
            cameraTransform = cam != null ? cam.transform : (Camera.main != null ? Camera.main.transform : null);
        }

        if (cameraTransform == null)
        {
            Debug.LogWarning("HeldItem : aucune caméra trouvée, glisse-la dans « Camera Transform ».", this);
            enabled = false;
            return;
        }

        var go = new GameObject("HeldItemPivot");
        pivot = go.transform;
        pivot.SetParent(cameraTransform, false);
    }

    void OnDestroy()
    {
        if (pivot != null) Destroy(pivot.gameObject);
    }

    void Update()
    {
        if (pivot == null || inventory == null || world == null) return;

        ItemStack current = inventory.SelectedStack;
        bool changed = inventory.Selected != shownSlot
                    || current.IsEmpty != (!hasShown || shown.IsEmpty)
                    || (!current.IsEmpty && current.type != shown.type);

        if (changed && equipTime < 0f)
        {
            // Lancer l'animation de changement : l'objet descend, on échange le modèle en bas, il remonte
            equipTime = 0f;
            equipSwapped = false;
            shownSlot = inventory.Selected;
        }

        // Entrée : un coup en maintenant le clic gauche, ou un coup à chaque clic droit (poser / utiliser)
        bool active = Cursor.lockState == CursorLockMode.Locked && !Inventory.IsOpen && (player == null || !player.InputLocked);
        if (active)
        {
            if (swingTime < 0f && (Input.GetMouseButton(0) || Input.GetMouseButtonDown(1))) swingTime = 0f;
        }

        // Équipement
        float equipOffset = 0f;
        if (equipTime >= 0f)
        {
            equipTime += Time.deltaTime;
            float half = equipDuration * 0.5f;
            float k = equipTime < half ? equipTime / half : 1f - (equipTime - half) / half; // 0 -> 1 -> 0
            equipOffset = Mathf.Clamp01(k);

            if (!equipSwapped && equipTime >= half)
            {
                equipSwapped = true;
                Rebuild(inventory.SelectedStack);
            }
            if (equipTime >= equipDuration) equipTime = -1f;
        }

        // Si l'objet change pendant qu'on ne lance pas d'animation (ex. dernier objet consommé), on remplace direct
        if (equipTime < 0f && hasShown && !SameType(shown, inventory.SelectedStack))
            Rebuild(inventory.SelectedStack);

        if (model == null) return;

        // Balancement de la marche
        Vector3 v = player != null ? player.Velocity : Vector3.zero;
        float speed = new Vector2(v.x, v.z).magnitude;
        bobPhase += Time.deltaTime * bobSpeed * Mathf.Clamp01(speed / 4f);
        float amp = bobAmount * Mathf.Clamp01(speed / 4f);
        Vector3 bob = new Vector3(Mathf.Sin(bobPhase) * amp, -Mathf.Abs(Mathf.Cos(bobPhase)) * amp, 0f);

        // Coup : l'objet part vers l'avant et vers le bas, puis revient
        Vector3 swingPos = Vector3.zero;
        Vector3 swingRot = Vector3.zero;
        if (swingTime >= 0f)
        {
            swingTime += Time.deltaTime;
            float t = Mathf.Clamp01(swingTime / swingDuration);
            float s = Mathf.Sin(t * Mathf.PI);
            float s2 = Mathf.Sin(Mathf.Sqrt(t) * Mathf.PI);
            swingPos = new Vector3(-0.15f * s2, -0.1f * Mathf.Sin(t * Mathf.PI * 2f), 0.12f * s);
            swingRot = new Vector3(-55f * s, 18f * s2, -20f * s);
            if (swingTime >= swingDuration) swingTime = -1f;
        }

        bool isBlock = !shown.IsEmpty && shown.IsBlock;
        Vector3 pos = isBlock ? blockPosition : shownIsTool ? toolPosition : itemPosition;
        Vector3 rot = isBlock ? blockRotation : shownIsTool ? toolRotation : itemRotation;

        pivot.localPosition = pos + bob + swingPos + new Vector3(0f, -0.6f * equipOffset, 0f);
        pivot.localRotation = Quaternion.Euler(rot + swingRot);

        bool visible = !(hideWhenInventoryOpen && Inventory.IsOpen);
        if (model.activeSelf != visible) model.SetActive(visible);
    }

    static bool SameType(ItemStack a, ItemStack b)
    {
        if (a.IsEmpty || b.IsEmpty) return a.IsEmpty && b.IsEmpty;
        return a.type == b.type;
    }

    void Rebuild(ItemStack stack)
    {
        if (model != null) Destroy(model);
        model = null;
        shown = stack;
        hasShown = true;
        shownSlot = inventory.Selected;
        if (stack.IsEmpty) return;

        shownIsTool = !stack.IsBlock && ItemDatabase.Get(stack.type).tool != ToolKind.None;
        float size = stack.IsBlock ? blockSize : shownIsTool ? toolSize : itemSize;
        model = ItemModel.Create(pivot, world, stack, size, false);
        int layer = LayerMask.NameToLayer("Overlay");
        model.layer = layer;

        // Ni ombre portée, ni ombre reçue (le joueur ne doit pas s'ombrer lui-même)
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
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
