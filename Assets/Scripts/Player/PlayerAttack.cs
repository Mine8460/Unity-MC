using UnityEngine;

// Clic gauche sur un monstre proche : on le frappe. Les dégâts viennent de l'objet tenu (champ « Attack Damage »
// des fichiers d'objets ; pioches, haches et pelles intégrées en ont aussi). Coup critique en tombant.
// À mettre sur le joueur (ou n'importe quel objet).
public class PlayerAttack : MonoBehaviour
{
    [SerializeField] PlayerController player;
    [SerializeField] Inventory inventory;
    [SerializeField] Camera cam;
    [SerializeField] float reach = 3.5f;
    [Tooltip("Dégâts à mains nues (si l'objet tenu n'a pas de dégâts)")]
    [SerializeField] float handDamage = 1f;
    [SerializeField] float cooldown = 0.45f;
    [Tooltip("Multiplicateur quand on frappe en tombant (comme Minecraft : 1,5)")]
    [SerializeField] float criticalMultiplier = 1.5f;
    float next;

    void Start()
    {
        if (cam == null) cam = Camera.main;
        if (player == null) player = GetComponent<PlayerController>();
        if (inventory == null) inventory = GetComponent<Inventory>();
#if UNITY_2023_1_OR_NEWER
        if (inventory == null) inventory = FindFirstObjectByType<Inventory>();
#else
        if (inventory == null) inventory = FindObjectOfType<Inventory>();
#endif
    }

    void Update()
    {
        if (cam == null || !Input.GetMouseButtonDown(0) || Time.time < next) return;
        if (player != null && player.InputLocked) return;

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        RaycastHit[] hits = Physics.RaycastAll(ray, reach, Physics.AllLayers, QueryTriggerInteraction.Collide);
        float best = float.MaxValue;
        Mob mob = null;
        for (int i = 0; i < hits.Length; i++)
        {
            Mob m = hits[i].collider.GetComponent<Mob>();
            if (m != null && hits[i].distance < best) { best = hits[i].distance; mob = m; }
        }
        if (mob == null) return;

        // Un bloc plus près que le monstre le cache
        for (int i = 0; i < hits.Length; i++)
            if (hits[i].collider.GetComponent<Chunk>() != null && hits[i].distance < best) return;

        float damage = handDamage;
        if (inventory != null)
        {
            ItemStack stack = inventory.SelectedStack;
            if (!stack.IsEmpty)
            {
                ItemInfo info = ItemDatabase.Get(stack.type);
                if (info.attackDamage > 0f) damage = info.attackDamage;
                if (info.durability > 0) inventory.DamageSelected(info.tool == ToolKind.Sword ? 1 : 2);
            }
        }
        if (player != null && player.Velocity.y < -0.5f) damage *= criticalMultiplier;

        next = Time.time + cooldown;
        mob.Hurt(damage, transform.position);
    }
}
