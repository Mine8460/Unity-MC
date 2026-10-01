using UnityEngine;

// Objet au sol : un petit bloc qui tourne, tombe, rebondit sur le terrain et se ramasse en s'approchant.
// La position du GameObject est le CENTRE DU BAS de sa boîte de collision.
public class ItemEntity : MonoBehaviour
{
    const float HitboxSize = 0.25f;
    const float Gravity = 22f;
    const float MaxFallSpeed = 40f;
    const float MaxStep = 0.2f;        // déplacement max par sous-étape (inférieur à la boîte)
    const float ModelScale = 0.3f;
    const float PickupRadius = 1.1f;
    const float MagnetRadius = 2.4f;
    const float MagnetSpeed = 7f;
    const float LifeTime = 300f;       // secondes

    World world;
    BlockType type;
    int count;
    Vector3 velocity;
    float age;
    float pickupDelay;
    bool grounded;
    Transform pivot;

    public BlockType Type => type;
    public int Count => count;

    public void Init(World world, BlockType type, int count, Vector3 velocity, float pickupDelay)
    {
        this.world = world;
        this.type = type;
        this.count = count;
        this.velocity = velocity;
        this.pickupDelay = pickupDelay;

        // Le pivot tourne sur lui-même ; le modèle est décalé pour que le pivot soit son centre
        pivot = new GameObject("Pivot").transform;
        pivot.SetParent(transform, false);

        var model = new GameObject("Model");
        model.transform.SetParent(pivot, false);
        model.transform.localScale = Vector3.one * ModelScale;
        model.transform.localPosition = Vector3.one * (-ModelScale * 0.5f);

        model.AddComponent<MeshFilter>().sharedMesh = world.GetBlockMesh(type);
        var meshRenderer = model.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = world.ChunkMaterial;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void Update()
    {
        age += Time.deltaTime;
        if (age > LifeTime)
        {
            Destroy(gameObject);
            return;
        }

        // Animation : tourne et flotte légèrement une fois posé
        float bob = grounded ? Mathf.Sin(age * 2.5f) * 0.04f : 0f;
        pivot.localPosition = new Vector3(0f, ModelScale * 0.5f + 0.06f + bob, 0f);
        pivot.localRotation = Quaternion.Euler(0f, age * 90f, 0f);

        // Chunk non chargé : l'objet est gelé (il ne tombe pas dans le vide)
        Vector3 pos = transform.position;
        if (!world.IsLoaded(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z))) return;

        float dt = Mathf.Min(Time.deltaTime, 0.05f);

        // Le joueur est-il assez près pour attirer l'objet ?
        bool magnet = false;
        Vector3 toPlayer = Vector3.zero;
        float distance = float.MaxValue;
        Inventory inventory = world.PlayerInventory;

        if (inventory != null && age >= pickupDelay && inventory.CanAccept(type))
        {
            Vector3 target = inventory.transform.position + Vector3.up * 0.9f;
            toPlayer = target - (pos + Vector3.up * 0.15f);
            distance = toPlayer.magnitude;
            magnet = distance < MagnetRadius;
        }

        Simulate(dt, magnet, toPlayer);

        // Ramassage
        if (inventory != null && age >= pickupDelay)
        {
            Vector3 target = inventory.transform.position + Vector3.up * 0.9f;
            if ((target - (transform.position + Vector3.up * 0.15f)).sqrMagnitude < PickupRadius * PickupRadius)
            {
                int left = inventory.Add(type, count);
                if (left <= 0) Destroy(gameObject);
                else count = left;
            }
        }
    }

    void Simulate(float dt, bool magnet, Vector3 toPlayer)
    {
        if (magnet)
        {
            // Attiré vers le joueur, sans gravité
            velocity = Vector3.MoveTowards(velocity, toPlayer.normalized * MagnetSpeed, 40f * dt);
        }
        else
        {
            velocity.y = Mathf.Max(velocity.y - Gravity * dt, -MaxFallSpeed);

            // Frottement horizontal : fort au sol, faible en l'air
            float friction = Mathf.Exp(-(grounded ? 8f : 0.4f) * dt);
            velocity.x *= friction;
            velocity.z *= friction;
        }

        Vector3 pos = transform.position;
        MoveAxis(ref pos, 1, velocity.y * dt);
        MoveAxis(ref pos, 0, velocity.x * dt);
        MoveAxis(ref pos, 2, velocity.z * dt);
        transform.position = pos;

        // Au sol ? (test juste sous les pieds, indépendant du framerate)
        grounded = velocity.y <= 0.01f &&
                   VoxelCollision.Collide(world, pos + Vector3.down * 0.02f, HitboxSize, HitboxSize, -1, 0f, out _);

        if (pos.y < -30f) Destroy(gameObject); // tombé hors du monde
    }

    void MoveAxis(ref Vector3 pos, int axis, float delta)
    {
        if (delta == 0f) return;

        int steps = Mathf.CeilToInt(Mathf.Abs(delta) / MaxStep);
        float step = delta / steps;

        for (int i = 0; i < steps; i++)
        {
            pos[axis] += step;
            if (!VoxelCollision.Collide(world, pos, HitboxSize, HitboxSize, axis, step, out float snap)) continue;

            pos[axis] = snap;
            velocity[axis] = 0f;
            break;
        }
    }
}
