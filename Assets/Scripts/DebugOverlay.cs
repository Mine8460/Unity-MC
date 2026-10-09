using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Mode debug à la F3 de Minecraft. À ajouter sur un objet de la scène (par exemple celui du World).
//   F3        : affiche / cache les infos
//   F3 + G    : bordures de chunks
//   F3 + B    : boîtes de collision des entités (monstres, objets) avec la ligne des yeux (rouge) et le regard (bleu)
//   F3 + H    : boîtes de collision des blocs autour du joueur
// Pour les lignes, garde le fichier DebugLines.shader dans un dossier Resources.
public class DebugOverlay : MonoBehaviour
{
    [SerializeField] World world;
    [SerializeField] PlayerController player;
    [SerializeField] PlayerStats stats;
    [SerializeField] Inventory inventory;
    [SerializeField] DayNightCycle dayNight;
    [SerializeField] Camera cam;
    [SerializeField] KeyCode key = KeyCode.F3;
    [SerializeField] int fontDivisor = 55;

    bool showInfo = true;
    bool showChunks, showHitboxes, showBlockBoxes;
    bool comboUsed;
    float fps, fpsMin = 999f, fpsMax;
    float fpsTimer;
    int frames;
    float frameTimeMs;
    int chunkCount;
    float chunkCountTimer;
    readonly StringBuilder left = new StringBuilder(2048);
    readonly StringBuilder right = new StringBuilder(1024);
    GUIStyle style, bgStyle;
    Texture2D bg;
    Material lineMat;
    Mesh lineMesh;
    readonly List<Vector3> lineVerts = new List<Vector3>(4096);
    readonly List<Color> lineColors = new List<Color>(4096);
    readonly List<int> lineIdx = new List<int>(4096);
    MobSpawner spawner;
    int spawnIndex;
    string toast;
    float toastUntil;

    void Start()
    {
        if (world == null) world = Find<World>();
        if (player == null) player = Find<PlayerController>();
        if (stats == null) stats = Find<PlayerStats>();
        if (inventory == null) inventory = Find<Inventory>();
        if (dayNight == null) dayNight = Find<DayNightCycle>();
        if (cam == null) cam = Camera.main;
        spawner = Find<MobSpawner>();
    }

    static T Find<T>() where T : Object
    {
        return FindAnyObjectByType<T>();
    }

    // ------------------------------------------------------------------
    // Touches
    // ------------------------------------------------------------------

    void Update()
    {
        if (Input.GetKeyDown(key)) comboUsed = false;
        if (Input.GetKey(key))
        {
            if (Input.GetKeyDown(KeyCode.G)) { showChunks = !showChunks; comboUsed = true; }
            if (Input.GetKeyDown(KeyCode.B)) { showHitboxes = !showHitboxes; comboUsed = true; }
            if (Input.GetKeyDown(KeyCode.H)) { showBlockBoxes = !showBlockBoxes; comboUsed = true; }
            if (Input.GetKeyDown(KeyCode.T)) { ToggleTime(); comboUsed = true; }
            if (Input.GetKeyDown(KeyCode.M)) { SpawnMob(); comboUsed = true; }
            if (Input.GetKeyDown(KeyCode.K)) { KillMobs(); comboUsed = true; }
            if (Input.GetKeyDown(KeyCode.L)) { ToggleSpawner(); comboUsed = true; }
            if (Input.GetKeyDown(KeyCode.C)) { Heal(); comboUsed = true; }
        }
        if (Input.GetKeyUp(key) && !comboUsed) showInfo = !showInfo;

        // FPS : moyenne sur 0,5 s, avec min et max
        float dt = Time.unscaledDeltaTime;
        frames++;
        fpsTimer += dt;
        frameTimeMs = Mathf.Lerp(frameTimeMs, dt * 1000f, 0.1f);
        if (fpsTimer >= 0.5f)
        {
            fps = frames / fpsTimer;
            fpsMin = Mathf.Min(fpsMin, fps);
            fpsMax = Mathf.Max(fpsMax, fps);
            frames = 0; fpsTimer = 0f;
        }
        chunkCountTimer -= dt;
        if (chunkCountTimer <= 0f)
        {
            chunkCountTimer = 1f;
            chunkCount = CountChunks();
            fpsMin = 999f; fpsMax = 0f; // min / max sur la dernière seconde
        }
    }

    // ------------------------------------------------------------------
    // Raccourcis pour les testeurs
    // ------------------------------------------------------------------

    void Toast(string text) { toast = text; toastUntil = Time.unscaledTime + 3f; }

    void ToggleTime()
    {
        if (dayNight == null) return;
        // Jour -> début de nuit ; nuit -> matin
        dayNight.TimeOfDay = dayNight.IsNight ? 0.02f : 0.5f;
        Toast(dayNight.IsNight ? "Heure : minuit" : "Heure : matin");
    }

    void KillMobs()
    {
        int n = 0;
        for (int i = Mob.All.Count - 1; i >= 0; i--)
            if (Mob.All[i] != null) { Destroy(Mob.All[i].gameObject); n++; }
        Toast(n + " monstre(s) supprimé(s)");
    }

    void ToggleSpawner()
    {
        if (spawner == null) { Toast("Pas de MobSpawner dans la scène"); return; }
        spawner.SpawnEnabled = !spawner.SpawnEnabled;
        Toast("Apparition des monstres : " + (spawner.SpawnEnabled ? "activée" : "désactivée"));
    }

    void Heal()
    {
        if (stats == null) return;
        stats.Heal(20f);
        stats.Eat(20, 20f);
        Toast("Vie et faim restaurées");
    }

    // Fait apparaître un monstre devant le joueur (appuie plusieurs fois pour changer de type)
    void SpawnMob()
    {
        if (world == null || cam == null || MobDatabase.All.Count == 0) return;
        MobDefinition def = MobDatabase.All[spawnIndex % MobDatabase.All.Count];
        spawnIndex++;
        Vector3 pos;
        RaycastHit hit;
        if (Physics.Raycast(new Ray(cam.transform.position, cam.transform.forward), out hit, 10f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            pos = hit.point + hit.normal * 0.05f;
        else
            pos = cam.transform.position + cam.transform.forward * 4f;
        Mob.Create(world, def, stats, dayNight, pos);
        Toast("Apparition : " + (string.IsNullOrEmpty(def.displayName) ? def.name : def.displayName));
    }

    static int CountChunks()
    {
        return FindObjectsByType<Chunk>().Length;
    }

    // ------------------------------------------------------------------
    // Texte
    // ------------------------------------------------------------------

    static string Facing(float yaw)
    {
        float y = Mathf.Repeat(yaw + 22.5f, 360f);
        int i = (int)(y / 45f) % 8;
        string[] names = { "nord (+Z)", "nord-est", "est (+X)", "sud-est", "sud (-Z)", "sud-ouest", "ouest (-X)", "nord-ouest" };
        return names[i];
    }

    void BuildText()
    {
        left.Length = 0; right.Length = 0;
        Vector3 p = player != null ? player.transform.position : Vector3.zero;
        int bx = Mathf.FloorToInt(p.x), by = Mathf.FloorToInt(p.y), bz = Mathf.FloorToInt(p.z);
        int cx = Mathf.FloorToInt(bx / (float)Chunk.SizeX), cz = Mathf.FloorToInt(bz / (float)Chunk.SizeZ);

        left.AppendLine("Voxel Game");
        left.AppendFormat("{0:0} fps  (min {1:0} / max {2:0})  {3:0.0} ms\n", fps, fpsMin > 998f ? fps : fpsMin, fpsMax, frameTimeMs);
        left.AppendFormat("Chunks chargés : {0}   Monstres : {1}   Objets : {2}\n", chunkCount, Mob.All.Count, ItemEntity.All.Count);
        left.AppendLine();

        if (player != null)
        {
            left.AppendFormat("XYZ : {0:0.000} / {1:0.000} / {2:0.000}\n", p.x, p.y, p.z);
            left.AppendFormat("Bloc : {0} {1} {2}   Dans le chunk : {3} {4} {5}\n", bx, by, bz, bx & Chunk.MaskXZ, by, bz & Chunk.MaskXZ);
            left.AppendFormat("Chunk : {0} {1}\n", cx, cz);

            float pitch = cam != null ? NormalizePitch(cam.transform.eulerAngles.x) : 0f;
            float yaw = player.Yaw;
            left.AppendFormat("Orientation : {0}  (yaw {1:0.0} / pitch {2:0.0})\n", Facing(yaw), Mathf.DeltaAngle(0f, yaw), pitch);

            Vector3 v = player.Velocity;
            left.AppendFormat("Vitesse : {0:0.00} / {1:0.00} / {2:0.00}  ({3:0.00} blocs/s)\n", v.x, v.y, v.z, new Vector2(v.x, v.z).magnitude);
            left.AppendFormat("Vol : {0}   Sprint : {1}   Eau : {2}{3}\n", player.IsFlying ? "oui" : "non", player.IsSprinting ? "oui" : "non",
                              player.InWater ? "oui" : "non", player.EyeUnderwater ? " (tête dedans)" : "");
        }

        if (world != null)
        {
            left.AppendLine();
            if (world.IsLoaded(bx, bz))
            {
                left.AppendFormat("Lumière : {0} (ciel {1}, bloc {2})\n",
                                  Mathf.Max(world.GetSkyLight(bx, by, bz), world.GetBlockLight(bx, by, bz)), world.GetSkyLight(bx, by, bz), world.GetBlockLight(bx, by, bz));
                left.AppendFormat("Sous les pieds : {0}\n", BlockDatabase.Name(world.GetBlock(bx, by - 1, bz)));
            }
            else left.AppendLine("Chunk non chargé");

            BlockHit(out string target, out string mobTarget);
            left.AppendLine();
            if (mobTarget != null) left.AppendLine(mobTarget);
            if (target != null) left.AppendLine(target);
        }

        if (dayNight != null)
        {
            left.AppendLine();
            left.AppendFormat("Heure : {0:00}:{1:00}   Lumière du jour : {2:0.00}   {3}\n", Mathf.FloorToInt(dayNight.Hours),
                              Mathf.FloorToInt((dayNight.Hours % 1f) * 60f), dayNight.Daylight, dayNight.IsNight ? "nuit" : "jour");
        }

        if (stats != null)
            left.AppendFormat("Vie : {0:0.0}   Faim : {1}\n", stats.Health, stats.Food);
        if (inventory != null && !inventory.SelectedStack.IsEmpty)
        {
            ItemStack s = inventory.SelectedStack;
            left.AppendFormat("Objet tenu : {0} x{1}{2}\n", ItemDatabase.Get(s.type).name ?? s.type.ToString(), s.count, s.damage > 0 ? "  usure " + s.damage : "");
        }

        // Colonne de droite : système
        right.AppendFormat("Unity {0}\n", Application.unityVersion);
        right.AppendFormat("{0}\n", SystemInfo.graphicsDeviceName);
        right.AppendFormat("{0} x {1}  ({2})\n", Screen.width, Screen.height, SystemInfo.graphicsDeviceType);
        right.AppendFormat("CPU : {0}  ({1} threads)\n", SystemInfo.processorType, SystemInfo.processorCount);
        right.AppendFormat("Mémoire : {0} Mo alloués / {1} Mo\n", (System.GC.GetTotalMemory(false) / 1048576), SystemInfo.systemMemorySize);
        right.AppendFormat("Atlas : {0} tuiles de {1} px\n", BlockDatabase.AtlasTilesPerRow * BlockDatabase.AtlasTilesPerRow, BlockDatabase.TilePixels);
        right.AppendLine();
        right.AppendLine("Raccourcis (maintenir F3) :");
        right.AppendFormat("  G  Bordures de chunks  [{0}]\n", showChunks ? "OUI" : "non");
        right.AppendFormat("  B  Hitboxes des entités  [{0}]\n", showHitboxes ? "OUI" : "non");
        right.AppendFormat("  H  Hitboxes des blocs  [{0}]\n", showBlockBoxes ? "OUI" : "non");
        right.AppendLine("  T  Jour / nuit");
        right.AppendLine("  M  Faire apparaître un monstre");
        right.AppendLine("  K  Supprimer tous les monstres");
        right.AppendFormat("  L  Apparition auto des monstres  [{0}]\n", spawner == null ? "?" : (spawner.SpawnEnabled ? "OUI" : "non"));
        right.AppendLine("  C  Soigner et nourrir");
        right.AppendLine("F3 seul : cacher ce menu");

        if (Mob.All.Count > 0 && player != null)
        {
            right.AppendLine();
            right.AppendLine("Monstres proches :");
            int shown = 0;
            for (int i = 0; i < Mob.All.Count && shown < 6; i++)
            {
                Mob m = Mob.All[i];
                if (m == null || m.def == null) continue;
                float d = Vector3.Distance(m.transform.position, p);
                right.AppendFormat("  {0}  {1:0} m  PV {2:0}\n", string.IsNullOrEmpty(m.def.displayName) ? m.def.name : m.def.displayName, d, m.Health);
                shown++;
            }
        }
    }

    static float NormalizePitch(float x) { return x > 180f ? x - 360f : x; }

    // Bloc ou monstre visé
    void BlockHit(out string blockText, out string mobText)
    {
        blockText = null; mobText = null;
        if (cam == null) return;
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        RaycastHit[] hits = Physics.RaycastAll(ray, 8f, Physics.AllLayers, QueryTriggerInteraction.Collide);
        float bestBlock = float.MaxValue, bestMob = float.MaxValue;
        RaycastHit blockHit = default(RaycastHit);
        Mob mob = null;
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].collider.GetComponent<Chunk>() != null && hits[i].distance < bestBlock) { bestBlock = hits[i].distance; blockHit = hits[i]; }
            Mob m = hits[i].collider.GetComponent<Mob>();
            if (m != null && hits[i].distance < bestMob) { bestMob = hits[i].distance; mob = m; }
        }
        if (mob != null && bestMob < bestBlock)
            mobText = string.Format("Vise : {0}  PV {1:0}/{2:0}  ({3:0.0} m)", mob.def.name, mob.Health, mob.def.health, bestMob);
        if (bestBlock < float.MaxValue)
        {
            Vector3 q = blockHit.point - blockHit.normal * 0.01f;
            int x = Mathf.FloorToInt(q.x), y = Mathf.FloorToInt(q.y), z = Mathf.FloorToInt(q.z);
            BlockType t = world.GetBlock(x, y, z);
            blockText = string.Format("Bloc visé : {0}  (id {1}, état {2})  {3} {4} {5}\nLumière devant : ciel {6} / bloc {7}",
                                      BlockDatabase.Name(t), (int)t, world.GetState(x, y, z), x, y, z,
                                      world.GetSkyLight(x + Mathf.RoundToInt(blockHit.normal.x), y + Mathf.RoundToInt(blockHit.normal.y), z + Mathf.RoundToInt(blockHit.normal.z)),
                                      world.GetBlockLight(x + Mathf.RoundToInt(blockHit.normal.x), y + Mathf.RoundToInt(blockHit.normal.y), z + Mathf.RoundToInt(blockHit.normal.z)));
        }
    }

    void InitStyle()
    {
        if (style == null)
            style = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, normal = { textColor = Color.white } };
        if (bg == null)
        {
            bg = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            bg.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 0.55f));
            bg.Apply();
        }
    }

    void OnGUI()
    {
        if (toast != null && Time.unscaledTime < toastUntil)
        {
            InitStyle();
            style.fontSize = Mathf.Max(11, Screen.height / Mathf.Max(20, fontDivisor));
            Vector2 size = style.CalcSize(new GUIContent(toast));
            GUI.Label(new Rect((Screen.width - size.x) * 0.5f, Screen.height * 0.82f, size.x + 8, size.y), toast, style);
        }
        if (!showInfo) return;
        InitStyle();
        style.fontSize = Mathf.Max(11, Screen.height / Mathf.Max(20, fontDivisor));

        BuildText();
        DrawColumn(left.ToString(), 4f, false);
        DrawColumn(right.ToString(), 4f, true);
    }

    // Message court en bas de l'écran après un raccourci


    void DrawColumn(string text, float margin, bool alignRight)
    {
        string[] lines = text.Split('\n');
        float y = margin;
        float lh = style.fontSize * 1.35f;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) { y += lh * 0.5f; continue; }
            Vector2 size = style.CalcSize(new GUIContent(line));
            float x = alignRight ? Screen.width - size.x - margin : margin;
            GUI.DrawTexture(new Rect(x - 2, y, size.x + 4, lh), bg);
            // ombre portée, comme Minecraft
            style.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(x + 1, y + 1, size.x + 8, lh), line, style);
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(x, y, size.x + 8, lh), line, style);
            y += lh;
        }
    }

    // ------------------------------------------------------------------
    // Lignes : un mesh de lignes reconstruit à chaque image et dessiné normalement (fonctionne avec URP,
    // et les lignes sont cachées par les blocs comme dans Minecraft)
    // ------------------------------------------------------------------

    Material LineMaterial()
    {
        if (lineMat != null) return lineMat;
        Shader sh = Resources.Load<Shader>("DebugLines");
        if (sh == null) sh = Shader.Find("Hidden/Voxel/DebugLines");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;
        lineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        lineMat.renderQueue = 3000;
        return lineMat;
    }

    void LateUpdate()
    {
        lineVerts.Clear(); lineColors.Clear(); lineIdx.Clear();
        if (!showChunks && !showHitboxes && !showBlockBoxes) return;

        if (showChunks) DrawChunkBorders();
        if (showHitboxes) DrawEntityBoxes();
        if (showBlockBoxes) DrawBlockBoxes();
        if (lineVerts.Count == 0) return;

        Material m = LineMaterial();
        if (m == null) return;
        if (lineMesh == null)
        {
            lineMesh = new Mesh { name = "Debug lines", hideFlags = HideFlags.HideAndDontSave };
            lineMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            lineMesh.MarkDynamic();
        }
        lineMesh.Clear();
        lineMesh.SetVertices(lineVerts);
        lineMesh.SetColors(lineColors);
        lineMesh.SetIndices(lineIdx, MeshTopology.Lines, 0, false);
        lineMesh.bounds = new Bounds(cam != null ? cam.transform.position : Vector3.zero, Vector3.one * 100000f);
        Graphics.DrawMesh(lineMesh, Matrix4x4.identity, m, 0);
    }

    void Line(Vector3 a, Vector3 b, Color col)
    {
        int i = lineVerts.Count;
        lineVerts.Add(a); lineVerts.Add(b);
        lineColors.Add(col); lineColors.Add(col);
        lineIdx.Add(i); lineIdx.Add(i + 1);
    }

    void DrawBox(Vector3 min, Vector3 max, Color col)
    {
        Vector3 a = new Vector3(min.x, min.y, min.z), b = new Vector3(max.x, min.y, min.z), c = new Vector3(max.x, min.y, max.z), d = new Vector3(min.x, min.y, max.z);
        Vector3 up = new Vector3(0f, max.y - min.y, 0f);
        Line(a, b, col); Line(b, c, col); Line(c, d, col); Line(d, a, col);
        Line(a + up, b + up, col); Line(b + up, c + up, col); Line(c + up, d + up, col); Line(d + up, a + up, col);
        Line(a, a + up, col); Line(b, b + up, col); Line(c, c + up, col); Line(d, d + up, col);
    }

    void DrawChunkBorders()
    {
        if (player == null) return;
        Vector3 p = player.transform.position;
        int cx = Mathf.FloorToInt(p.x / Chunk.SizeX), cz = Mathf.FloorToInt(p.z / Chunk.SizeZ);
        float top = Chunk.SizeY;
        Color blue = new Color(0.25f, 0.45f, 1f, 0.9f), yellow = new Color(1f, 0.9f, 0.1f, 0.95f), green = new Color(0.2f, 1f, 0.3f, 0.8f);

        // Coins des chunks voisins (bleu) : lignes verticales sur toute la hauteur
        const int R = 2;
        for (int i = -R; i <= R + 1; i++)
            for (int j = -R; j <= R + 1; j++)
            {
                float x = (cx + i) * Chunk.SizeX, z = (cz + j) * Chunk.SizeZ;
                bool current = (i == 0 || i == 1) && (j == 0 || j == 1);
                Line(new Vector3(x, 0f, z), new Vector3(x, top, z), current ? yellow : blue);
            }

        // Chunk courant : cadres horizontaux tous les 16 blocs (sections), en vert, et sol/plafond
        float x0 = cx * Chunk.SizeX, z0 = cz * Chunk.SizeZ, x1 = x0 + Chunk.SizeX, z1 = z0 + Chunk.SizeZ;
        for (float y = 0f; y <= top; y += 16f)
        {
            Color col = (y == 0f || y == top) ? yellow : green;
            Line(new Vector3(x0, y, z0), new Vector3(x1, y, z0), col);
            Line(new Vector3(x1, y, z0), new Vector3(x1, y, z1), col);
            Line(new Vector3(x1, y, z1), new Vector3(x0, y, z1), col);
            Line(new Vector3(x0, y, z1), new Vector3(x0, y, z0), col);
        }

        // Quadrillage de 1 bloc sur les 4 parois du chunk courant, près du joueur (± 8 blocs de haut), pour repérer les coordonnées
        float yMin = Mathf.Max(0f, Mathf.Floor(p.y) - 8f), yMax = Mathf.Min(top, Mathf.Floor(p.y) + 8f);
        Color grid = new Color(1f, 0.9f, 0.1f, 0.18f);
        for (int k = 0; k <= Chunk.SizeX; k++)
        {
            Line(new Vector3(x0 + k, yMin, z0), new Vector3(x0 + k, yMax, z0), grid);
            Line(new Vector3(x0 + k, yMin, z1), new Vector3(x0 + k, yMax, z1), grid);
        }
        for (int k = 0; k <= Chunk.SizeZ; k++)
        {
            Line(new Vector3(x0, yMin, z0 + k), new Vector3(x0, yMax, z0 + k), grid);
            Line(new Vector3(x1, yMin, z0 + k), new Vector3(x1, yMax, z0 + k), grid);
        }
        for (float y = yMin; y <= yMax; y += 1f)
        {
            Line(new Vector3(x0, y, z0), new Vector3(x1, y, z0), grid);
            Line(new Vector3(x0, y, z1), new Vector3(x1, y, z1), grid);
            Line(new Vector3(x0, y, z0), new Vector3(x0, y, z1), grid);
            Line(new Vector3(x1, y, z0), new Vector3(x1, y, z1), grid);
        }
    }

    void DrawEntityBoxes()
    {
        // Monstres : boîte blanche, ligne des yeux rouge, regard bleu
        for (int i = 0; i < Mob.All.Count; i++)
        {
            Mob m = Mob.All[i];
            if (m == null || m.def == null) continue;
            Vector3 pos = m.transform.position;
            float hw = m.def.width * 0.5f, h = m.def.height;
            DrawBox(new Vector3(pos.x - hw, pos.y, pos.z - hw), new Vector3(pos.x + hw, pos.y + h, pos.z + hw), Color.white);

            float eye = pos.y + h * 0.9f;
            Line(new Vector3(pos.x - hw, eye, pos.z - hw), new Vector3(pos.x + hw, eye, pos.z - hw), Color.red);
            Line(new Vector3(pos.x + hw, eye, pos.z - hw), new Vector3(pos.x + hw, eye, pos.z + hw), Color.red);
            Line(new Vector3(pos.x + hw, eye, pos.z + hw), new Vector3(pos.x - hw, eye, pos.z + hw), Color.red);
            Line(new Vector3(pos.x - hw, eye, pos.z + hw), new Vector3(pos.x - hw, eye, pos.z - hw), Color.red);

            Vector3 eyePos = new Vector3(pos.x, eye, pos.z);
            Line(eyePos, eyePos + m.Facing * 2f, new Color(0.3f, 0.5f, 1f, 1f));
        }

        // Objets au sol : petite boîte blanche
        for (int i = 0; i < ItemEntity.All.Count; i++)
        {
            ItemEntity it = ItemEntity.All[i];
            if (it == null) continue;
            Vector3 pos = it.transform.position;
            const float hw = 0.125f, h = 0.25f;
            DrawBox(new Vector3(pos.x - hw, pos.y, pos.z - hw), new Vector3(pos.x + hw, pos.y + h, pos.z + hw), Color.white);
        }

        // Joueur (utile en caméra libre) : boîte 0,6 x 1,8, yeux en rouge
        if (player != null)
        {
            Vector3 pos = player.transform.position;
            DrawBox(new Vector3(pos.x - 0.3f, pos.y, pos.z - 0.3f), new Vector3(pos.x + 0.3f, pos.y + 1.8f, pos.z + 0.3f), new Color(1f, 1f, 1f, 0.5f));
        }
    }

    // Boîtes de collision des blocs dans un cube de 5 x 5 x 5 autour du joueur
    void DrawBlockBoxes()
    {
        if (player == null || world == null) return;
        Vector3 p = player.transform.position;
        int bx = Mathf.FloorToInt(p.x), by = Mathf.FloorToInt(p.y), bz = Mathf.FloorToInt(p.z);
        for (int x = bx - 3; x <= bx + 3; x++)
            for (int y = by - 3; y <= by + 4; y++)
                for (int z = bz - 3; z <= bz + 3; z++)
                {
                    BlockType t = world.GetBlock(x, y, z);
                    if (t == BlockType.Air) continue;
                    BlockInfo info = BlockDatabase.Get(t);
                    Box[] boxes = info.orientation == Orientation.None ? info.collisionBoxes : BlockDatabase.CollisionBoxes(t, world.GetState(x, y, z));
                    if (boxes == null) continue;
                    for (int i = 0; i < boxes.Length; i++)
                        DrawBox(new Vector3(x, y, z) + boxes[i].min, new Vector3(x, y, z) + boxes[i].max, new Color(0.3f, 1f, 0.6f, 0.9f));
                }
    }
}
