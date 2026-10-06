using UnityEngine;
using UnityEngine.InputSystem;

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
    [Tooltip("Au lancement, pose le joueur sur le sol (le terrain est généré : sa hauteur n'est pas connue à l'avance)")]
    [SerializeField] bool spawnOnSurface = true;
    [Tooltip("Hauteur max montée sans sauter (0,5 = dalles ; Minecraft : 0,6)")]
    [SerializeField] float stepHeight = 0.6f;

    [Header("Vol (pour explorer)")]
    [Tooltip("Active / coupe le vol")]
    [SerializeField] KeyCode flyToggleKey = KeyCode.F;
    [Tooltip("Double-appui sur la touche de saut pour activer / couper le vol, comme Minecraft")]
    [SerializeField] bool doubleTapJumpToFly = true;
    [SerializeField] float flySpeed = 11f;
    [Tooltip("Vitesse en maintenant la touche d'accélération")]
    [SerializeField] float flyBoostSpeed = 30f;
    [SerializeField] float flyVerticalSpeed = 8f;
    [Tooltip("Plus haut = s'arrête et repart plus sèchement")]
    [SerializeField] float flyAcceleration = 60f;
    [Tooltip("En vol (saut = monter)")]
    [SerializeField] bool noClip = false;
    [SerializeField] KeyCode noClipKey = KeyCode.N;

    [Header("Nage")]
    [Tooltip("Vitesse horizontale dans l'eau, en fraction de la marche")]
    [SerializeField, Range(0.1f, 1f)] float swimSpeedFactor = 0.6f;
    [Tooltip("Dans l'eau, la gravité est réduite à cette fraction : on coule lentement")]
    [SerializeField, Range(0f, 1f)] float waterGravityFactor = 0.25f;
    [SerializeField] float swimSinkSpeed = 3f;
    [Tooltip("Vitesse de remontée en maintenant la touche de saut")]
    [SerializeField] float swimUpSpeed = 4.5f;
    [Tooltip("Vitesse max à laquelle le courant entraîne le joueur")]
    [SerializeField] float waterPushSpeed = 2.5f;
    [Tooltip("Rapidité avec laquelle le courant prend (et lâche) le joueur")]
    [SerializeField] float waterPushAcceleration = 6f;
    [Tooltip("Voile de couleur quand la tête est sous l'eau (alpha 0 = aucun)")]
    [SerializeField] Color underwaterTint = new Color(0.1f, 0.25f, 0.6f, 0.45f);

    [Header("Souris")]
    [SerializeField] float mouseSensitivity = 2f;

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
    bool spawned;     // le joueur a été posé sur le sol (une seule fois, quand son chunk est prêt)
    bool flying;
    bool inWater;         // le corps est dans l'eau (nage)
    bool eyeInWater;      // la tête est sous la surface (voile bleu)
    bool blockedSideways; // un mur a arrêté le déplacement horizontal pendant cette frame
    Vector3 waterPush;    // vitesse donnée par le courant (arrive et repart progressivement)

    // Doit être identique à LiquidSurface dans ChunkMesher : la surface de l'eau est à 14/16 du bloc
    const float WaterSurface = 14f / 16f;
    float lastJumpPress = -10f; // moment du dernier appui sur saut (double-appui = vol)

    const float DoubleTapTime = 0.3f;

    public bool IsFlying => flying;

    InputAction moveAction;
    InputAction runAction;
    InputAction sneakAction;
    InputAction jumpAction;

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

    private void Start()
    {
        moveAction = InputSystem.actions.FindAction("Move");
        runAction = InputSystem.actions.FindAction("Run");
        sneakAction = InputSystem.actions.FindAction("Sneak");
        jumpAction = InputSystem.actions.FindAction("Jump");

        jumpAction.performed += OnJump;
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

            //if (Input.GetKeyDown(jumpKey))
            //{


            //    if (Input.GetKeyDown(flyToggleKey)) SetFlying(!flying);
            //    if (flying && Input.GetKeyDown(noClipKey)) noClip = !noClip;
            //}

            Simulate(Mathf.Min(Time.deltaTime, 0.05f), active);
            eyeInWater = IsEyeUnderwater();
        }
    }
    void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            jumpBuffer = JumpBufferTime;

            // Double-appui sur saut : active / coupe le vol
            if (doubleTapJumpToFly && Time.time - lastJumpPress < DoubleTapTime)
            {
                SetFlying(!flying);
                lastJumpPress = -10f;
                jumpBuffer = 0f;
            }
            else
            {
                lastJumpPress = Time.time;
            }
        }
    }

    // Pose le joueur sur le plus haut bloc solide sous sa hitbox (les 4 coins : il ne doit chevaucher aucun bloc).
    // Retourne false si l'un des coins est sur un chunk pas encore chargé : on réessaiera à la frame suivante.
    bool TryPlaceOnSurface()
    {
        Vector3 p = transform.position;

        if (spawnOnSurface)
        {
            float half = width * 0.5f;
            float ground = 0f;

            for (int i = 0; i < 4; i++)
            {
                int cx = Mathf.FloorToInt(p.x + ((i & 1) == 0 ? -half : half));
                int cz = Mathf.FloorToInt(p.z + ((i & 2) == 0 ? -half : half));
                if (!world.IsLoaded(cx, cz)) return false;
                ground = Mathf.Max(ground, world.GetSurfaceY(cx, cz));
            }

            p.y = ground + 0.01f;
            transform.position = p;
            velocity = Vector3.zero;
        }

        spawnPoint = p; // on réapparaît ici si on tombe hors du monde
        return true;
    }

    void Simulate(float dt, bool active)
    {
        // Le chunk sous le joueur n'est pas encore chargé (le monde se génère en arrière-plan) :
        // on attend, sinon il tomberait dans le vide.
        if (!world.IsLoaded(Mathf.FloorToInt(transform.position.x), Mathf.FloorToInt(transform.position.z))) return;

        // Première fois que le sol est prêt : on y pose le joueur
        if (!spawned)
        {
            if (!TryPlaceOnSurface()) return; // une partie de la hitbox est sur un chunk pas encore chargé
            spawned = true;
        }

        if (flying)
        {
            SimulateFlight(dt, active);
            return;
        }

        // --- Vitesse voulue ---
        Vector3 input = Vector3.zero;
        Vector2 move = moveAction.ReadValue<Vector2>();
        if (active)
        {
            input.x = move.x;
            input.z = move.y;
        }

        inWater = IsInWater(transform.position);

        // Courant : il entraîne le joueur, en plus de sa propre marche
        Vector3 pushTarget = inWater ? WaterFlowAround(transform.position) * waterPushSpeed : Vector3.zero;
        waterPush = Vector3.MoveTowards(waterPush, pushTarget, waterPushAcceleration * dt);

        Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * input;
        bool sprint = runAction.ReadValue<float>() > 0f;
        float speed = sprint ? sprintSpeed : walkSpeed;
        if (inWater) speed *= swimSpeedFactor;
        velocity.x = dir.x * speed + waterPush.x;
        velocity.z = dir.z * speed + waterPush.z;

        // Au sol ? On teste juste sous les pieds : fiable quel que soit le framerate
        Vector3 pos = transform.position;
        bool onGround = velocity.y <= 0f && Collide(pos + Vector3.down * GroundProbe, -1, 0f, out _);
        wasOnGround = onGround;

        // Saut : appui mémorisé un court instant, ou touche maintenue (comme Minecraft)
        bool wantsJump = active && (jumpBuffer > 0f || jumpAction.ReadValue<float>() > 0f);
        jumpBuffer = Mathf.Max(jumpBuffer - dt, 0f);

        bool swimUp = active && jumpAction.ReadValue<float>() > 0f;

        if (inWater)
        {
            // Nage : on coule lentement (l'eau amortit aussi les chutes) ; saut maintenu = on remonte
            // (dans une cascade, waterPush.y < 0 : on coule plus vite, et remonter est plus dur)
            if (swimUp)
                velocity.y = Mathf.MoveTowards(velocity.y, swimUpSpeed + waterPush.y, gravity * 2f * dt);
            else
                velocity.y = Mathf.Max(velocity.y - gravity * waterGravityFactor * dt, -swimSinkSpeed + Mathf.Min(0f, waterPush.y));
            jumpBuffer = 0f;
        }
        else
        {
            if (wantsJump && onGround)
            {
                velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);
                jumpBuffer = 0f;
            }

            velocity.y = Mathf.Max(velocity.y - gravity * dt, -maxFallSpeed);
        }

        // --- Déplacement axe par axe (Y, X, Z comme Minecraft) ---
        // Résoudre chaque axe séparément fait glisser le long des murs.
        blockedSideways = false;
        MoveAxis(ref pos, 1, velocity.y * dt);
        MoveAxis(ref pos, 0, velocity.x * dt);
        MoveAxis(ref pos, 2, velocity.z * dt);
        transform.position = pos;

        // Sortir de l'eau : en nageant vers le haut contre une berge, on se hisse dessus (comme Minecraft)
        if (inWater && swimUp && blockedSideways)
            velocity.y = Mathf.Max(velocity.y, Mathf.Sqrt(2f * gravity * jumpHeight));

        // Sécurité : tombé hors du monde
        if (pos.y < respawnBelowY)
        {
            transform.position = spawnPoint;
            velocity = Vector3.zero;
        }
    }

    // ------------------------------------------------------------------
    // Vol
    // ------------------------------------------------------------------

    void SetFlying(bool on)
    {
        if (flying == on) return;
        flying = on;
        velocity = Vector3.zero;

        // En sortant du mode fantôme à l'intérieur d'un bloc, on remonte jusqu'à une case libre
        if (!on && Collide(transform.position, -1, 0f, out _))
            EscapeUpwards();
    }

    void SimulateFlight(float dt, bool active)
    {
        Vector3 input = Vector3.zero;
        float vertical = 0f;
        bool boost = false;

        Vector2 move = moveAction.ReadValue<Vector2>();
        if (active)
        {
            input.x = move.x;
            input.z = move.y;
            vertical = (jumpAction.ReadValue<float>() > 0f ? 1f : 0f) - (sneakAction.ReadValue<bool>() ? 1f : 0f);
            boost = runAction.ReadValue<float>() > 0f;
        }

        // Vitesse voulue (le regard ne fait pas monter ou descendre : seules les touches le font, comme Minecraft)
        Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * input;
        float speed = boost ? flyBoostSpeed : flySpeed;
        float vSpeed = flyVerticalSpeed * (boost ? 2f : 1f);
        Vector3 target = new Vector3(dir.x * speed, vertical * vSpeed, dir.z * speed);

        // Petite inertie : on rejoint la vitesse voulue progressivement
        velocity = Vector3.MoveTowards(velocity, target, flyAcceleration * (boost ? 2f : 1f) * dt);

        Vector3 pos = transform.position;

        if (noClip)
        {
            pos += velocity * dt;
        }
        else
        {
            wasOnGround = false; // pas de montée automatique en vol
            MoveAxis(ref pos, 1, velocity.y * dt);
            MoveAxis(ref pos, 0, velocity.x * dt);
            MoveAxis(ref pos, 2, velocity.z * dt);
        }

        // Ne pas sortir du monde par le haut ou par le bas
        pos.y = Mathf.Clamp(pos.y, 1f, Chunk.SizeY + 32f);
        transform.position = pos;

        // Comme Minecraft : en descendant, toucher le sol arrête le vol
        if (!noClip && vertical < 0f && Collide(pos + Vector3.down * GroundProbe, -1, 0f, out _))
            SetFlying(false);
    }

    // Remonte case par case jusqu'à ce que la hitbox ne touche plus aucun bloc
    void EscapeUpwards()
    {
        Vector3 p = transform.position;
        for (int i = 0; i < Chunk.SizeY + 2 && Collide(p, -1, 0f, out _); i++)
            p.y = Mathf.Floor(p.y) + 1f;
        transform.position = p;
    }

    // ------------------------------------------------------------------
    // Eau
    // ------------------------------------------------------------------

    bool IsLiquidAt(Vector3 p)
    {
        BlockType type = world.GetBlock(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        return BlockDatabase.GetRef(type).shape == BlockShape.Liquid;
    }

    // Sens du courant autour du corps (pieds et mi-hauteur), de longueur 1, ou zéro
    Vector3 WaterFlowAround(Vector3 feet)
    {
        Vector3 sum = Vector3.zero;
        foreach (float h in new[] { 0.1f, height * 0.5f })
        {
            Vector3 p = feet + Vector3.up * h;
            sum += world.GetFlow(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        }
        return sum.sqrMagnitude > 1e-6f ? sum.normalized : Vector3.zero;
    }

    // Le corps est dans l'eau : aux pieds ou à mi-hauteur
    bool IsInWater(Vector3 feet)
    {
        return IsLiquidAt(feet + Vector3.up * 0.1f) || IsLiquidAt(feet + Vector3.up * (height * 0.5f));
    }

    // La tête est sous la surface (dans le bloc du dessus d'une étendue d'eau, la surface est à 14/16)
    bool IsEyeUnderwater()
    {
        Vector3 eye = transform.position + Vector3.up;
        if (!IsLiquidAt(eye)) return false;
        if (IsLiquidAt(eye + Vector3.up)) return true;
        return eye.y - Mathf.Floor(eye.y) < WaterSurface;
    }

    // Voile bleu sous l'eau, et petit rappel à l'écran quand on vole
    void OnGUI()
    {
        if (eyeInWater && underwaterTint.a > 0f)
        {
            Color previous = GUI.color;
            GUI.color = underwaterTint;
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        if (!flying) return;
        GUI.Label(new Rect(10f, 10f, 400f, 22f), noClip
            ? "Vol (fantôme) — N : collisions, F : arrêter"
            : "Vol — Espace / Shift : monter / descendre, Ctrl : vite, N : fantôme, F : arrêter");
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
            if (axis != 1) blockedSideways = true;
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
