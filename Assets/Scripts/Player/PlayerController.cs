using UnityEngine;

// Joueur "à la Minecraft" : hitbox AABB qui ne tourne jamais, collision calculée
// directement contre la grille de blocs (aucun Rigidbody, aucune friction, aucun accrochage).
[DisallowMultipleComponent]
public class PlayerController : MonoBehaviour
{
    [Header("Références")]
    [SerializeField] public World world;
    [Tooltip("La caméra, placée EN ENFANT de ce GameObject")]
    [SerializeField] Transform cameraTransform;

    [Header("Hitbox (comme Minecraft) - le pivot est aux PIEDS")]
    [SerializeField] float width = 0.6f;
    [SerializeField] float height = 1.8f;

    [Header("Mouvement")]
    [SerializeField] float walkSpeed = 4.3f;
    [SerializeField] float sprintSpeed = 5.6f;
    [SerializeField] float jumpHeight = 1.25f;
    [SerializeField] float gravity = 32f;
    [SerializeField] float maxFallSpeed = 50f;
    [SerializeField] float respawnBelowY = -30f;

    [Header("Souris")]
    [SerializeField] float mouseSensitivity = 2f;

    [Header("Touches (AZERTY par défaut)")]
    [SerializeField] KeyCode forwardKey = KeyCode.Z;
    [SerializeField] KeyCode backKey = KeyCode.S;
    [SerializeField] KeyCode leftKey = KeyCode.Q;
    [SerializeField] KeyCode rightKey = KeyCode.D;
    [SerializeField] KeyCode jumpKey = KeyCode.Space;
    [SerializeField] KeyCode sprintKey = KeyCode.LeftShift;

    // Marge minuscule : la hitbox est testée légèrement rétrécie, ce qui évite
    // de "toucher" les blocs voisins à cause des erreurs d'arrondi.
    const float Skin = 0.0001f;
    // Déplacement maximal par sous-étape (doit rester inférieur à 1 bloc)
    const float MaxStep = 0.4f;
    // Distance sous les pieds testée pour savoir si on est au sol (indépendante du framerate)
    const float GroundProbe = 0.02f;
    // Un appui sur saut juste avant l'atterrissage reste mémorisé ce temps (en secondes)
    const float JumpBufferTime = 0.15f;

    Vector3 velocity;
    Vector3 spawnPoint;
    float yaw;
    float pitch;
    float jumpBuffer; // temps restant pendant lequel un appui sur saut est mémorisé
    // (l'état "au sol" est recalculé à chaque frame dans Simulate)

    void Awake()
    {
        spawnPoint = transform.position;

        if (TryGetComponent(out Rigidbody _))
            Debug.LogWarning("PlayerController : supprime le Rigidbody et le BoxCollider du joueur, ils ne sont plus utilisés.", this);

        if (cameraTransform != null)
        {
            yaw = cameraTransform.eulerAngles.y;
        }

        LockCursor(true);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) LockCursor(false);
        if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked) LockCursor(true);
        bool active = Cursor.lockState == CursorLockMode.Locked;

        if (active)
        {
            // Seule la caméra tourne, jamais le corps
            yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
            pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
            pitch = Mathf.Clamp(pitch, -90f, 90f);
            cameraTransform.localRotation = Quaternion.Euler(pitch, yaw, 0f);

            if (Input.GetKeyDown(jumpKey)) jumpBuffer = JumpBufferTime;
        }

        if (Collides(transform.position))
        {
            return;
        }
        Simulate(Mathf.Min(Time.deltaTime, 0.05f), active);
    }

    void Simulate(float dt, bool active)
    {
        // --- Vitesse voulue ---
        Vector3 input = Vector3.zero;
        if (active)
        {
            input = new Vector3(
                (Input.GetKey(rightKey) ? 1f : 0f) - (Input.GetKey(leftKey) ? 1f : 0f),
                0f,
                (Input.GetKey(forwardKey) ? 1f : 0f) - (Input.GetKey(backKey) ? 1f : 0f));
            input = Vector3.ClampMagnitude(input, 1f);
        }

        Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * input;
        float speed = Input.GetKey(sprintKey) ? sprintSpeed : walkSpeed;
        velocity.x = dir.x * speed;
        velocity.z = dir.z * speed;

        // Au sol ? On teste juste sous les pieds : fiable quel que soit le framerate
        Vector3 pos = transform.position;
        bool onGround = velocity.y <= 0f && Collides(pos + Vector3.down * GroundProbe);

        // Saut : appui mémorisé un court instant, ou touche maintenue (comme Minecraft)
        bool wantsJump = active && (jumpBuffer > 0f || Input.GetKey(jumpKey));
        jumpBuffer = Mathf.Max(jumpBuffer - dt, 0f);

        if (wantsJump && onGround)
        {
            velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);
            jumpBuffer = 0f;
        }

        velocity.y = Mathf.Max(velocity.y - gravity * dt, -maxFallSpeed);

        // --- Déplacement axe par axe (Y, X, Z comme Minecraft) ---
        // Résoudre chaque axe séparément fait glisser le long des murs.
        MoveAxis(ref pos, 1, velocity.y * dt);
        MoveAxis(ref pos, 0, velocity.x * dt);
        MoveAxis(ref pos, 2, velocity.z * dt);
        transform.position = pos;

        // Sécurité : tombé hors du monde
        if (pos.y < respawnBelowY)
        {
            transform.position = spawnPoint;
            velocity = Vector3.zero;
        }
    }

    void MoveAxis(ref Vector3 pos, int axis, float delta)
    {
        if (delta == 0f) return;

        float hw = width * 0.5f;
        int steps = Mathf.CeilToInt(Mathf.Abs(delta) / MaxStep);
        float step = delta / steps;

        for (int i = 0; i < steps; i++)
        {
            pos[axis] += step;
            if (!Collides(pos)) continue;

            // Collision : on colle la hitbox au bord du bloc et on annule la vitesse sur cet axe
            if (axis == 1)
            {
                if (step > 0f) pos.y = Mathf.Floor(pos.y + height) - height;   // tête contre le plafond
                else pos.y = Mathf.Floor(pos.y) + 1f;     // pieds sur le sol
            }
            else
            {
                if (step > 0f) pos[axis] = Mathf.Floor(pos[axis] + hw) - hw;
                else pos[axis] = Mathf.Floor(pos[axis] - hw) + 1f + hw;
            }

            velocity[axis] = 0f;
            break;
        }
    }

    // La hitbox (pieds en p) touche-t-elle au moins un bloc solide ?
    bool Collides(Vector3 p)
    {
        float hw = width * 0.5f;

        int x0 = Mathf.FloorToInt(p.x - hw + Skin);
        int x1 = Mathf.FloorToInt(p.x + hw - Skin);
        int y0 = Mathf.FloorToInt(p.y + Skin);
        int y1 = Mathf.FloorToInt(p.y + height - Skin);
        int z0 = Mathf.FloorToInt(p.z - hw + Skin);
        int z1 = Mathf.FloorToInt(p.z + hw - Skin);

        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    if (BlockDatabase.Get(world.GetBlock(x, y, z)).collidable)
                        return true;
                }
        return false;
    }

    static void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(transform.position + Vector3.up * (height * 0.5f), new Vector3(width, height, width));
    }
}