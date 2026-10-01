using UnityEngine;

// Joueur "à la Minecraft" : hitbox AABB qui ne tourne jamais, collision calculée
// directement contre les boîtes de collision des blocs (aucun Rigidbody, aucune friction).
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
    [Tooltip("Hauteur max montée sans sauter (0,5 = dalles ; Minecraft : 0,6)")]
    [SerializeField] float stepHeight = 0.6f;

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
    // Déplacement maximal par sous-étape (doit rester inférieur à la largeur de la hitbox)
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
    bool wasOnGround; // au sol au début de la frame : autorise la montée automatique

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
        if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked && !Inventory.IsOpen) LockCursor(true);
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
        bool onGround = velocity.y <= 0f && Collide(pos + Vector3.down * GroundProbe, -1, 0f, out _);
        wasOnGround = onGround;

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

        int steps = Mathf.CeilToInt(Mathf.Abs(delta) / MaxStep);
        float step = delta / steps;

        for (int i = 0; i < steps; i++)
        {
            pos[axis] += step;
            if (!Collide(pos, axis, step, out float snap)) continue;

            // Obstacle bas (dalle...) : on monte dessus sans sauter, comme dans Minecraft
            if (axis != 1 && wasOnGround && TryStepUp(ref pos)) continue;

            // Collision : on colle la hitbox contre la boîte rencontrée et on annule la vitesse sur cet axe
            pos[axis] = snap;
            velocity[axis] = 0f;
            break;
        }
    }

    // `pos` est la position APRÈS un déplacement horizontal bloqué (en collision). Si l'obstacle fait au plus
    // stepHeight de haut et qu'il y a la place au-dessus, pose le joueur sur lui et renvoie true.
    bool TryStepUp(ref Vector3 pos)
    {
        Vector3 lifted = pos;
        lifted.y += stepHeight;
        if (Collide(lifted, -1, 0f, out _)) return false; // pas la place au-dessus : mur trop haut ou plafond bas

        // On redescend : la hauteur où poser les pieds est le dessus de l'obstacle
        Vector3 probe = lifted;
        probe.y -= stepHeight;
        float feetY = pos.y;
        if (Collide(probe, 1, -1f, out float surface)) feetY = Mathf.Max(pos.y, surface);

        var stepped = new Vector3(pos.x, feetY, pos.z);
        if (Collide(stepped, -1, 0f, out _)) return false;

        pos = stepped;
        return true;
    }

    // Teste la hitbox (pieds en p) contre les boîtes de collision des blocs.
    // Si axis >= 0, calcule aussi `snap` : la position sur cet axe où coller la hitbox
    // pour la débloquer, selon le sens du déplacement `dir`.
    bool Collide(Vector3 p, int axis, float dir, out float snap)
    {
        float hw = width * 0.5f;
        Vector3 min = new Vector3(p.x - hw, p.y, p.z - hw);
        Vector3 max = new Vector3(p.x + hw, p.y + height, p.z + hw);

        int x0 = Mathf.FloorToInt(min.x + Skin), x1 = Mathf.FloorToInt(max.x - Skin);
        int y0 = Mathf.FloorToInt(min.y + Skin), y1 = Mathf.FloorToInt(max.y - Skin);
        int z0 = Mathf.FloorToInt(min.z + Skin), z1 = Mathf.FloorToInt(max.z - Skin);

        bool hit = false;
        snap = dir > 0f ? float.PositiveInfinity : float.NegativeInfinity;

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            Box[] boxes = BlockDatabase.Get(world.GetBlock(x, y, z)).collisionBoxes;
            if (boxes == null) continue;

            var cell = new Vector3(x, y, z);
            for (int i = 0; i < boxes.Length; i++)
            {
                Vector3 bmin = cell + boxes[i].min;
                Vector3 bmax = cell + boxes[i].max;

                // Chevauchement (la marge Skin ignore les simples contacts)
                if (min.x + Skin >= bmax.x || max.x - Skin <= bmin.x) continue;
                if (min.y + Skin >= bmax.y || max.y - Skin <= bmin.y) continue;
                if (min.z + Skin >= bmax.z || max.z - Skin <= bmin.z) continue;

                hit = true;
                if (axis < 0) return true;

                // Position qui colle la hitbox contre cette boîte ; on garde la plus restrictive
                float candidate;
                if (dir > 0f)
                    candidate = bmin[axis] - (axis == 1 ? height : hw);
                else
                    candidate = bmax[axis] + (axis == 1 ? 0f : hw);

                snap = dir > 0f ? Mathf.Min(snap, candidate) : Mathf.Max(snap, candidate);
            }
        }

        if (!hit) snap = 0f;
        return hit;
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
