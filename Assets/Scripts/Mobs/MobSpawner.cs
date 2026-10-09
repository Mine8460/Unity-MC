using UnityEngine;

// Apparition et disparition des monstres, comme Minecraft :
//  - toutes les `interval` secondes, on tire des positions au hasard dans un anneau autour du joueur
//    (entre minDistance et maxDistance) ; si la case a une lumière <= maxLight (0 = noir total), un sol
//    plein dessous et de la place pour le monstre, un groupe apparaît ;
//  - un monstre à plus de despawnDistance blocs disparaît ; entre randomDespawnDistance et despawnDistance
//    il peut disparaître au hasard.
// Ajoute ce composant sur n'importe quel objet de la scène (par exemple celui du World).
public class MobSpawner : MonoBehaviour
{
    [SerializeField] World world;
    [SerializeField] PlayerStats player;
    [SerializeField] DayNightCycle dayNight;

    [Header("Apparition")]
    [SerializeField] bool spawnEnabled = true;
    [SerializeField] float interval = 0.5f;
    [SerializeField] int attemptsPerTick = 12;
    [SerializeField] int maxMobs = 40;
    [Tooltip("Pas d'apparition plus près que ça du joueur")]
    [SerializeField] float minDistance = 24f;
    [Tooltip("Rayon de recherche autour du joueur (reste dans les chunks chargés)")]
    [SerializeField] float maxDistance = 48f;
    [Tooltip("Hauteur de recherche au-dessus et au-dessous du joueur")]
    [SerializeField] int verticalRange = 16;
    [Tooltip("De nuit, la lumière du ciel baisse de cette valeur (15 = nuit noire comme l'obscurité totale)")]
    [Range(0, 15)] [SerializeField] int nightSkyPenalty = 15;

    [Header("Disparition")]
    [SerializeField] float despawnDistance = 128f;
    [SerializeField] float randomDespawnDistance = 32f;
    [Tooltip("Chance par seconde de disparaître entre les deux distances")]
    [SerializeField] float randomDespawnChance = 0.05f;

    float timer;

    public bool SpawnEnabled { get { return spawnEnabled; } set { spawnEnabled = value; } }

    void Start()
    {
        if (world == null) world = Find<World>();
        if (player == null) player = Find<PlayerStats>();
        if (dayNight == null) dayNight = Find<DayNightCycle>();
    }

    static T Find<T>() where T : Object
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<T>();
#else
        return FindObjectOfType<T>();
#endif
    }

    void Update()
    {
        if (world == null || player == null) return;
        timer += Time.deltaTime;
        if (timer < interval) return;
        float dt = timer;
        timer = 0f;

        Vector3 pp = player.transform.position;
        Despawn(pp, dt);
        if (spawnEnabled && !player.IsDead && Mob.All.Count < maxMobs) TrySpawn(pp);
    }

    void Despawn(Vector3 pp, float dt)
    {
        for (int i = Mob.All.Count - 1; i >= 0; i--)
        {
            Mob m = Mob.All[i];
            if (m == null) continue;
            float d = Vector3.Distance(m.transform.position, pp);
            if (d > despawnDistance) { Destroy(m.gameObject); continue; }
            if (d > randomDespawnDistance && Random.value < randomDespawnChance * dt) Destroy(m.gameObject);
        }
    }

    // Lumière « utile » d'une case : torches, ou ciel (diminué la nuit)
    int EffectiveLight(int x, int y, int z)
    {
        int block = world.GetBlockLight(x, y, z);
        int sky = world.GetSkyLight(x, y, z);
        if (dayNight != null)
        {
            float night = Mathf.Clamp01((1f - dayNight.Daylight) / 0.8f);
            sky -= Mathf.RoundToInt(nightSkyPenalty * night);
        }
        return Mathf.Max(block, Mathf.Max(sky, 0));
    }

    bool Free(int x, int y, int z)
    {
        BlockInfo i = BlockDatabase.Get(world.GetBlock(x, y, z));
        return !i.collidable && i.shape != BlockShape.Liquid;
    }

    // Peut-on faire apparaître un monstre aux pieds de la case (x, y, z) ?
    bool CanSpawnAt(int x, int y, int z, Vector3 playerPos)
    {
        if (y < 1 || y >= Chunk.SizeY - 2) return false;
        if (!world.IsLoaded(x, z)) return false;
        if (!Free(x, y, z) || !Free(x, y + 1, z)) return false;
        BlockInfo below = BlockDatabase.Get(world.GetBlock(x, y - 1, z));
        if (below.shape != BlockShape.Cube || !below.collidable || !below.opaque) return false;
        Vector3 c = new Vector3(x + 0.5f, y, z + 0.5f);
        return Vector3.Distance(c, playerPos) >= minDistance;
    }

    static int CountOf(MobDefinition def)
    {
        int n = 0;
        for (int i = 0; i < Mob.All.Count; i++) if (Mob.All[i] != null && Mob.All[i].def == def) n++;
        return n;
    }

    MobDefinition Pick(int light, BlockType ground)
    {
        float total = 0f;
        var all = MobDatabase.All;
        for (int i = 0; i < all.Count; i++) if (Allowed(all[i], light, ground)) total += all[i].spawnWeight;
        if (total <= 0f) return null;
        float r = Random.value * total;
        for (int i = 0; i < all.Count; i++)
        {
            if (!Allowed(all[i], light, ground)) continue;
            r -= all[i].spawnWeight;
            if (r <= 0f) return all[i];
        }
        return null;
    }

    bool Allowed(MobDefinition d, int light, BlockType ground)
    {
        if (d == null || d.spawnWeight <= 0f || light < d.minLight || light > d.maxLight) return false;
        if (CountOf(d) >= d.maxCount) return false;
        if (d.spawnOnBlocks == null || d.spawnOnBlocks.Length == 0) return true;
        for (int i = 0; i < d.spawnOnBlocks.Length; i++)
            if (d.spawnOnBlocks[i] != null && (int)d.spawnOnBlocks[i].id == (int)ground) return true;
        return false;
    }

    void TrySpawn(Vector3 pp)
    {
        for (int a = 0; a < attemptsPerTick; a++)
        {
            float ang = Random.value * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Random.Range(minDistance * minDistance, maxDistance * maxDistance));
            int x = Mathf.FloorToInt(pp.x + Mathf.Cos(ang) * r);
            int z = Mathf.FloorToInt(pp.z + Mathf.Sin(ang) * r);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(pp.y) + Random.Range(-verticalRange, verticalRange + 1), 2, Chunk.SizeY - 3);

            // On descend jusqu'à trouver un sol
            int y = y0;
            if (!Free(x, y, z)) continue;
            int limit = 12;
            while (limit-- > 0 && y > 1 && Free(x, y - 1, z)) y--;

            if (!CanSpawnAt(x, y, z, pp)) continue;

            // Quel monstre ? Tirage pondéré parmi ceux qui acceptent cette lumière et ce sol
            int light = EffectiveLight(x, y, z);
            MobDefinition def = Pick(light, world.GetBlock(x, y - 1, z));
            if (def == null) continue;

            int count = Random.Range(def.groupMin, Mathf.Max(def.groupMin, def.groupMax) + 1);
            for (int k = 0; k < count && Mob.All.Count < maxMobs && CountOf(def) < def.maxCount; k++)
            {
                int gx = x + (k == 0 ? 0 : Random.Range(-3, 4));
                int gz = z + (k == 0 ? 0 : Random.Range(-3, 4));
                int gy = y;
                if (k > 0)
                {
                    gy = y + 1;
                    int lim = 3;
                    while (lim-- > 0 && gy > 1 && Free(gx, gy - 1, gz)) gy--;
                }
                if (!CanSpawnAt(gx, gy, gz, pp)) continue;
                if (EffectiveLight(gx, gy, gz) < def.minLight || EffectiveLight(gx, gy, gz) > def.maxLight) continue;
                Mob.Create(world, def, player, dayNight, new Vector3(gx + 0.5f, gy, gz + 0.5f));
            }
            return; // un groupe par cycle
        }
    }
}
