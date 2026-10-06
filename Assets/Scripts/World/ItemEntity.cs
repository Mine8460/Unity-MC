using UnityEngine;

// Objet au sol : un petit bloc (ou, pour un outil, un lingot..., son icône à plat) qui tourne, tombe,
// rebondit sur le terrain et se ramasse en s'approchant.
// La position du GameObject est le CENTRE DU BAS de sa boîte de collision.
public class ItemEntity : MonoBehaviour
{
    const float HitboxSize = 0.25f;
    const float Gravity = 22f;
    const float MaxFallSpeed = 40f;
    const float MaxStep = 0.2f;        // déplacement max par sous-étape (inférieur à la boîte)
    const float ModelScale = 0.3f;
    const float FlatScale = 0.45f;     // taille de l'icône d'un objet plat
    const float PickupRadius = 1.1f;
    const float MagnetRadius = 2.4f;
    const float MagnetSpeed = 7f;
    const float LifeTime = 300f;       // secondes

    World world;
    ItemStack stack;
    Vector3 velocity;
    float age;
    float pickupDelay;
    bool grounded;
    Transform pivot;

    public ItemStack Stack => stack;
    public ItemType Type => stack.type;
    public int Count => stack.count;

    static Mesh flatMesh; // carré à deux faces, partagé par tous les objets plats

    public void Init(World world, ItemStack stack, Vector3 velocity, float pickupDelay)
    {
        this.world = world;
        this.stack = stack;
        this.velocity = velocity;
        this.pickupDelay = pickupDelay;

        // Le pivot tourne sur lui-même ; le modèle est décalé pour que le pivot soit son centre
        pivot = new GameObject("Pivot").transform;
        pivot.SetParent(transform, false);

        var model = new GameObject("Model");
        model.transform.SetParent(pivot, false);
        var meshRenderer = model.AddComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        if (stack.IsBlock)
        {
            // Un bloc : son vrai mesh, en petit
            model.transform.localScale = Vector3.one * ModelScale;
            model.transform.localPosition = Vector3.one * (-ModelScale * 0.5f);
            model.AddComponent<MeshFilter>().sharedMesh = world.GetBlockMesh(stack.Block);
            meshRenderer.sharedMaterial = world.ChunkMaterial;
        }
        else
        {
            // Un objet : son icône, à plat (vue des deux côtés)
            model.transform.localScale = Vector3.one * FlatScale;
            model.AddComponent<MeshFilter>().sharedMesh = FlatMesh();
            meshRenderer.sharedMaterial = world.FlatItemMaterial;

            var block = new MaterialPropertyBlock();
            block.SetTexture("_BaseMap", ItemIcons.Get(stack.type));
            meshRenderer.SetPropertyBlock(block);
        }
    }

    // Carré de 1 x 1 centré, avec une face de chaque côté. Couleurs : pleine lumière du ciel, sans occlusion.
    static Mesh FlatMesh()
    {
        if (flatMesh != null) return flatMesh;

        var color = new Color32(0, 255, 255, 0);
        flatMesh = new Mesh { name = "Flat item" };
        flatMesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(-0.5f, -0.5f, 0f),
        };
        flatMesh.normals = new[]
        {
            Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
        };
        flatMesh.uv = new[]
        {
            new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
            new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f),
        };
        flatMesh.colors32 = new[] { color, color, color, color, color, color, color, color };
        flatMesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        flatMesh.RecalculateBounds();
        return flatMesh;
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

        if (inventory != null && age >= pickupDelay && inventory.CanAccept(stack))
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
                int left = inventory.Add(stack);
                if (left <= 0) Destroy(gameObject);
                else stack.count = left;
            }
        }
    }

    const float FloatSpeed = 1.2f;       // vitesse de remontée vers la surface
    const float WaterPushSpeed = 2.5f;   // vitesse max donnée par le courant
    const float WaterFallSpeed = 4f;     // vitesse de chute dans une cascade

    bool InWater(out Vector3 flow)
    {
        Vector3 p = transform.position + Vector3.up * 0.1f;
        int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
        flow = Vector3.zero;
        if (world.GetBlock(x, y, z) != BlockType.Water) return false;
        flow = world.GetFlow(x, y, z);
        return true;
    }

    void Simulate(float dt, bool magnet, Vector3 toPlayer)
    {
        if (magnet)
        {
            // Attiré vers le joueur, sans gravité
            velocity = Vector3.MoveTowards(velocity, toPlayer.normalized * MagnetSpeed, 40f * dt);
        }
        else if (InWater(out Vector3 flow))
        {
            // Dans l'eau (comme Minecraft) : l'objet flotte vers la surface et suit le courant ;
            // dans une cascade, il est emporté vers le bas
            float targetY = flow.y < -0.1f ? -WaterFallSpeed : FloatSpeed;
            velocity.y = Mathf.MoveTowards(velocity.y, targetY, 10f * dt);

            Vector3 horizontal = Vector3.MoveTowards(new Vector3(velocity.x, 0f, velocity.z),
                                                     new Vector3(flow.x, 0f, flow.z) * WaterPushSpeed, 6f * dt);
            velocity.x = horizontal.x;
            velocity.z = horizontal.z;
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
