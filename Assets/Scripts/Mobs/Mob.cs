using System.Collections.Generic;
using UnityEngine;

// Un monstre ou un animal vivant dans le monde. Ses caractéristiques viennent de son MobDefinition (fichier).
// Créé par MobSpawner.
public class Mob : MonoBehaviour
{
    public static readonly List<Mob> All = new List<Mob>();

    public MobDefinition def;
    public Vector3 Facing { get { return body != null ? body.forward : transform.forward; } }
    public float Health { get { return health; } }

    World world;
    PlayerStats target;
    DayNightCycle dayNight;
    MobModel model;
    Transform body;     // pivote vers où le monstre regarde ; la boîte de collision, elle, ne tourne jamais
    float yaw;
    Vector3 velocity;
    Vector3 knock;
    bool onGround;
    float health;
    float nextAttack, hurtTimer, burnTimer, wanderTimer, wanderYaw, fleeTimer, dyingTimer;
    Vector3 fleeFrom;
    bool dying;
    List<Vector3> path;
    int pathIdx;
    float repathTimer, unreachableTimer;

    const float Probe = 0.02f;

    public static Mob Create(World world, MobDefinition def, PlayerStats target, DayNightCycle dayNight, Vector3 pos)
    {
        var go = new GameObject(string.IsNullOrEmpty(def.displayName) ? def.name : def.displayName);
        go.transform.position = pos;
        go.layer = 2; // Ignore Raycast : ne gêne ni la casse ni la pose de blocs
        var mob = go.AddComponent<Mob>();
        mob.def = def;
        mob.world = world;
        mob.target = target;
        mob.dayNight = dayNight;
        mob.health = def.health;
        mob.body = new GameObject("Body").transform;
        mob.body.SetParent(go.transform, false);
        mob.body.gameObject.layer = 2;
        mob.yaw = Random.value * 360f;
        mob.body.localRotation = Quaternion.Euler(0, mob.yaw, 0);
        mob.model = MobModel.Build(def, mob.body);
        mob.wanderYaw = Random.value * 360f;

        var col = go.AddComponent<BoxCollider>();
        col.center = new Vector3(0, def.height * 0.5f, 0);
        col.size = new Vector3(def.width, def.height, def.width);
        col.isTrigger = true; // seulement pour être visé
        return mob;
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // ------------------------------------------------------------------
    // Vie
    // ------------------------------------------------------------------

    public void Hurt(float amount, Vector3 fromPos)
    {
        if (dying) return;
        health -= amount;
        hurtTimer = 0.3f;
        model.Trigger("Hurt");
        Vector3 dir = transform.position - fromPos; dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = body.forward;
        knock = dir.normalized * 6f;
        if (onGround) velocity.y = 5f;
        if (def.behavior == MobBehavior.Passive) { fleeTimer = 5f; fleeFrom = fromPos; }
        if (health <= 0f) Die();
    }

    void Die()
    {
        dying = true;
        dyingTimer = 0.7f;
        model.Trigger("Die");
        model.SetHurt(true);
        Destroy(GetComponent<BoxCollider>());

        if (def.drops != null)
        {
            foreach (MobDrop d in def.drops)
            {
                if (d == null || Random.value > d.chance) continue;
                int n = Random.Range(Mathf.Max(0, d.min), Mathf.Max(d.min, d.max) + 1);
                if (n <= 0) continue;
                Vector3 p = transform.position + Vector3.up * 0.5f;
                Vector3 v = new Vector3(Random.Range(-1.5f, 1.5f), 4f, Random.Range(-1.5f, 1.5f));
                if (d.item != null) world.DropItem((ItemType)d.item.id, n, p, v);
                else if (d.block != null) world.DropItem((BlockType)d.block.id, n, p, v);
            }
        }
    }

    // ------------------------------------------------------------------
    // Boucle
    // ------------------------------------------------------------------

    bool Hit(Vector3 p, int axis, float dir, out float snap) { return VoxelCollision.Collide(world, p, def.width, def.height, axis, dir, out snap); }

    void Update()
    {
        if (world == null || target == null || def == null) { Destroy(gameObject); return; }

        if (dying)
        {
            // Le monstre bascule sur le côté puis disparaît
            dyingTimer -= Time.deltaTime;
            float k = 1f - Mathf.Clamp01(dyingTimer / 0.7f);
            body.localRotation = Quaternion.Euler(0, yaw, Mathf.Lerp(0f, 90f, k));
            if (dyingTimer <= 0f) Destroy(gameObject);
            return;
        }

        Vector3 pos = transform.position;
        if (!world.IsLoaded(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z))) return; // chunk absent : on attend

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        onGround = velocity.y <= 0f && Hit(pos + Vector3.down * Probe, -1, 0f, out _);

        Vector3 toPlayer = target.transform.position - pos;
        toPlayer.y = 0f;
        float dist = toPlayer.magnitude;
        Vector3 wish = Vector3.zero;
        bool hostile = def.behavior == MobBehavior.Hostile;
        bool chasing = hostile && !target.IsDead && dist < def.followRange;
        float runBoost = 1f;

        bool jumpNow = false;
        if (chasing)
        {
            if (dist < def.attackRange * 0.7f) { path = null; }
            else
            {
                repathTimer -= dt;
                unreachableTimer -= dt;
                if (repathTimer <= 0f && unreachableTimer <= 0f && onGround)
                {
                    repathTimer = Random.Range(0.7f, 1.2f);
                    Vector3Int startCell, goalCell;
                    if (MobPathfinder.GroundCell(world, pos, def.width, def.height, out startCell))
                    {
                        MobPathfinder.GroundCell(world, target.transform.position, 0.6f, 1.8f, out goalCell);
                        List<Vector3> found = MobPathfinder.Find(world, startCell, goalCell, def.followRange + 8f, 1500, def.width, def.height);
                        if (found != null) { path = found; pathIdx = 0; }
                        else { path = null; unreachableTimer = 3f; } // inatteignable (grotte...) : il laisse tomber un moment
                    }
                }
                if (path != null) wish = Follow(pos, ref jumpNow);
            }
            // Pas de chemin : il erre au lieu de foncer dans un mur
            if (path == null && dist >= def.attackRange * 0.7f) wish = Wander(dt, pos, ref jumpNow);
        }
        else if (fleeTimer > 0f)
        {
            fleeTimer -= dt;
            Vector3 away = pos - fleeFrom; away.y = 0f;
            if (away.sqrMagnitude > 0.001f) wish = SafeDir(pos, away.normalized, ref jumpNow);
            runBoost = 1.6f;
        }
        else
        {
            path = null;
            wish = Wander(dt, pos, ref jumpNow);
        }

        if (wish.sqrMagnitude > 0.0001f)
        {
            float targetYaw = Mathf.Atan2(wish.x, wish.z) * Mathf.Rad2Deg;
            yaw = Mathf.LerpAngle(yaw, targetYaw, 10f * dt);
            body.localRotation = Quaternion.Euler(0, yaw, 0);
        }

        knock = Vector3.MoveTowards(knock, Vector3.zero, 18f * dt);
        Vector3 move = wish * def.speed * runBoost + knock;
        velocity.y -= 25f * dt;
        if (velocity.y < -40f) velocity.y = -40f;

        bool blocked = false;
        pos = MoveAxis(pos, 0, move.x * dt, ref blocked);
        pos = MoveAxis(pos, 2, move.z * dt, ref blocked);
        bool unused = false;
        pos = MoveAxis(pos, 1, velocity.y * dt, ref unused);

        // Saut : seulement quand le chemin (ou la case suivante) monte d'un bloc
        if (jumpNow && onGround) velocity.y = def.jumpSpeed;

        transform.position = pos;
        if (pos.y < -20f) { Destroy(gameObject); return; }

        if (hostile) Attack(dist);
        Burn(dt, pos);

        // Animation
        float flat = new Vector2(move.x, move.z).magnitude;
        float look = 0f;
        bool hasLook = false;
        if (dist < 16f && !target.IsDead)
        {
            Vector3 dir = toPlayer;
            float want = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            look = Mathf.DeltaAngle(yaw, want);
            hasLook = true;
        }
        model.Animate(dt, flat, Mathf.Clamp01(flat / Mathf.Max(def.speed, 0.01f)), look, hasLook);
        bool hurtNow = hurtTimer > 0f;
        hurtTimer = Mathf.Max(0f, hurtTimer - dt);
        if (hurtNow != hurtTimer > 0f || hurtNow) model.SetHurt(hurtTimer > 0f);
    }

    // Suit le chemin calculé
    Vector3 Follow(Vector3 pos, ref bool jump)
    {
        while (path != null && pathIdx < path.Count)
        {
            Vector3 wp = path[pathIdx];
            float dx = wp.x - pos.x, dz = wp.z - pos.z;
            if (dx * dx + dz * dz < 0.09f && Mathf.Abs(wp.y - pos.y) < 1.1f) pathIdx++;
            else break;
        }
        if (path == null || pathIdx >= path.Count) { path = null; return Vector3.zero; }

        Vector3 target3 = path[pathIdx];
        Vector3 d = target3 - pos; float up = d.y; d.y = 0f;
        if (up > 0.5f && d.magnitude < 1.3f + def.width * 0.5f) jump = true;
        return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.zero;
    }

    // Erre : avance dans une direction tant que la case suivante est praticable, sinon en choisit une autre
    Vector3 Wander(float dt, Vector3 pos, ref bool jump)
    {
        wanderTimer -= dt;
        if (wanderTimer <= 0f)
        {
            wanderTimer = Random.Range(2f, 5f);
            wanderYaw = Random.value < 0.4f ? float.NaN : Random.value * 360f;
        }
        if (float.IsNaN(wanderYaw)) return Vector3.zero;
        Vector3 dir = Quaternion.Euler(0, wanderYaw, 0) * Vector3.forward;
        Vector3 w = SafeDir(pos, dir, ref jump);
        if (w == Vector3.zero) wanderTimer = 0f; // obstacle : nouvelle direction au prochain tour
        return w * 0.4f;
    }

    // Garde la direction seulement si la case devant est praticable (marche, marche haute, ou petite descente)
    Vector3 SafeDir(Vector3 pos, Vector3 dir, ref bool jump)
    {
        Vector3Int cell;
        if (!MobPathfinder.GroundCell(world, pos, def.width, def.height, out cell)) return dir; // en l'air : on garde l'élan
        int dx = 0, dz = 0;
        if (Mathf.Abs(dir.x) > Mathf.Abs(dir.z)) dx = dir.x > 0 ? 1 : -1; else dz = dir.z > 0 ? 1 : -1;
        int ty;
        if (!MobPathfinder.Step(world, cell.x, cell.y, cell.z, dx, dz, def.width, def.height, out ty)) return Vector3.zero;
        if (ty > cell.y) jump = true;
        return dir;
    }

    Vector3 MoveAxis(Vector3 pos, int axis, float delta, ref bool blocked)
    {
        if (Mathf.Abs(delta) < 1e-6f) return pos;
        int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(delta) / 0.1f));
        float step = delta / steps;
        for (int i = 0; i < steps; i++)
        {
            pos[axis] += step;
            float snap;
            if (!Hit(pos, axis, step, out snap)) continue;
            pos[axis] = snap;
            if (axis == 1) velocity.y = 0f; else blocked = true;
            break;
        }
        return pos;
    }

    void Attack(float flatDist)
    {
        if (target.IsDead || Time.time < nextAttack) return;
        float dy = Mathf.Abs(target.transform.position.y - transform.position.y);
        if (flatDist > def.attackRange || dy > 1.8f) return;
        nextAttack = Time.time + def.attackCooldown;
        model.Trigger("Attack");
        target.Damage(def.attackDamage);
    }

    void Burn(float dt, Vector3 pos)
    {
        if (!def.burnInDaylight || dayNight == null || dayNight.Daylight < 0.85f) return;
        int x = Mathf.FloorToInt(pos.x), y = Mathf.FloorToInt(pos.y + def.height), z = Mathf.FloorToInt(pos.z);
        if (world.GetSkyLight(x, y, z) < 15) return;
        burnTimer += dt;
        if (burnTimer >= 1f) { burnTimer = 0f; Hurt(1f, pos + body.forward); knock = Vector3.zero; }
    }
}
