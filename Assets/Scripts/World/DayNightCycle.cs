using UnityEngine;

// Cycle jour/nuit façon Minecraft. À mettre sur un GameObject de la scène (par exemple celui du World).
//   - le soleil traverse le ciel, puis la lune (lumière faible et bleutée) ; ils se relaient sous l'horizon,
//     là où leur intensité est nulle : aucun saut de lumière ;
//   - la lumière du CIEL baisse la nuit (pas jusqu'au noir) ; celle des TORCHES ne change pas ;
//   - la couleur du ciel (fond de la caméra ou skybox) et du brouillard suit l'heure.
// Les shaders des blocs et de l'eau lisent l'obscurité dans une variable globale : aucun matériau n'est modifié.
public class DayNightCycle : MonoBehaviour
{
    [Header("Temps")]
    [Tooltip("Durée d'une journée complète, en minutes (Minecraft : 20)")]
    [SerializeField, Min(0.1f)] float dayLengthMinutes = 20f;
    [Tooltip("Heure au lancement : 0 = lever du soleil, 0.25 = midi, 0.5 = coucher, 0.75 = minuit")]
    [SerializeField, Range(0f, 1f)] float startTime = 0.05f;
    [Tooltip("Maintenir cette touche accélère le temps (pour tester)")]
    [SerializeField] KeyCode fastForwardKey = KeyCode.K;
    [SerializeField, Min(1f)] float fastForwardSpeed = 60f;

    [Header("Soleil et lune")]
    [Tooltip("Lumière directionnelle (vide = celle des Lighting Settings, ou la première trouvée)")]
    [SerializeField] Light sun;
    [SerializeField] float sunIntensity = 1.2f;
    [SerializeField] Color noonColor = new Color(1f, 0.96f, 0.88f);
    [SerializeField] Color sunsetColor = new Color(1f, 0.55f, 0.3f);
    [SerializeField] float moonIntensity = 0.3f;
    [SerializeField] Color moonColor = new Color(0.55f, 0.65f, 1f);
    [Tooltip("Inclinaison de la course du soleil (évite un soleil pile au-dessus, et donc des ombres sous les blocs)")]
    [SerializeField, Range(0f, 45f)] float sunTilt = 20f;
    [Tooltip("Orientation de la course du soleil (0 = se lève à l'est, +X)")]
    [SerializeField, Range(0f, 360f)] float sunYaw = 0f;
    [Tooltip("Le soleil avance par crans de cet angle (en degrés) : entre deux crans, les ombres sont immobiles, " +
             "ce qui supprime le scintillement de leurs bords. 0 = rotation continue.")]
    [SerializeField, Range(0f, 2f)] float sunAngleStep = 0.25f;

    [Header("Lumière du ciel")]
    [Tooltip("Lumière du ciel à minuit, en fraction du plein jour (Minecraft : environ 0,2)")]
    [SerializeField, Range(0f, 1f)] float nightSkyLight = 0.2f;

    [Header("Ciel")]
    [Tooltip("Utiliser le ciel Voxel/Sky : soleil et lune carrés qui bougent de façon fluide, étoiles la nuit")]
    [SerializeField] bool voxelSky = true;
    [Tooltip("Matériau du ciel (shader Voxel/Sky). Vide = créé automatiquement")]
    [SerializeField] Material skyMaterial;

    [Header("Couleur du ciel")]
    [SerializeField] Color daySky = new Color(0.52f, 0.74f, 1f);
    [SerializeField] Color sunsetSky = new Color(0.98f, 0.58f, 0.35f);
    [SerializeField] Color nightSky = new Color(0.02f, 0.03f, 0.08f);
    [Tooltip("Exposition de la skybox procédurale, le jour et la nuit")]
    [SerializeField] float daySkyboxExposure = 1.3f;
    [SerializeField] float nightSkyboxExposure = 0.08f;

    // 0 à 1 : 0 = lever du soleil, 0,25 = midi, 0,5 = coucher, 0,75 = minuit
    public float TimeOfDay { get; set; }

    // Heure de l'horloge (0 à 24) : 6 h au lever du soleil, comme Minecraft
    public float Hours => Mathf.Repeat(TimeOfDay * 24f + 6f, 24f);

    // Lumière du ciel actuelle (nightSkyLight à minuit, 1 en plein jour) : utile pour l'apparition de monstres
    public float Daylight { get; private set; } = 1f;

    public bool IsNight => Daylight < 0.5f * (1f + nightSkyLight);

    static readonly int DarknessId = Shader.PropertyToID("_VoxelDarkness");
    static readonly int ExposureId = Shader.PropertyToID("_Exposure");

    static readonly int SunDirId = Shader.PropertyToID("_VoxelSunDir");
    static readonly int SunAxisId = Shader.PropertyToID("_VoxelSunAxis");
    static readonly int ZenithId = Shader.PropertyToID("_VoxelZenithColor");
    static readonly int HorizonId = Shader.PropertyToID("_VoxelHorizonColor");
    static readonly int NightId = Shader.PropertyToID("_VoxelNight");

    Camera cam;
    Material originalSkybox, skyboxInstance;
    bool voxelSkyActive, createdSkyMaterial;
    CameraClearFlags previousClearFlags;

    void Start()
    {
        TimeOfDay = startTime;
        cam = Camera.main;

        if (sun == null) sun = RenderSettings.sun;
        if (sun == null)
        {
            foreach (Light l in FindLights())
                if (l.type == LightType.Directional) { sun = l; break; }
        }
        if (sun == null) Debug.LogWarning("DayNightCycle : aucune lumière directionnelle trouvée (champ Sun).", this);
        else RenderSettings.sun = sun; // la skybox procédurale dessine le soleil à sa position

        originalSkybox = RenderSettings.skybox;

        if (voxelSky)
        {
            if (skyMaterial == null)
            {
                Shader shader = Shader.Find("Voxel/Sky");
                if (shader != null) { skyMaterial = new Material(shader); createdSkyMaterial = true; }
                else Debug.LogWarning("DayNightCycle : shader Voxel/Sky introuvable (ajoute VoxelSky.shader au projet).", this);
            }

            if (skyMaterial != null)
            {
                RenderSettings.skybox = skyMaterial;
                if (cam != null)
                {
                    previousClearFlags = cam.clearFlags;
                    cam.clearFlags = CameraClearFlags.Skybox; // le ciel doit être dessiné comme skybox
                }
                voxelSkyActive = true;
            }
        }
        else if (originalSkybox != null && originalSkybox.HasProperty("_Exposure"))
        {
            // Copie de la skybox : on règle son exposition sans toucher au matériau du projet
            skyboxInstance = new Material(originalSkybox);
            RenderSettings.skybox = skyboxInstance;
        }

        Apply();
    }

    void OnDisable()
    {
        Shader.SetGlobalFloat(DarknessId, 0f); // retour au plein jour
        if (skyboxInstance != null)
        {
            RenderSettings.skybox = originalSkybox;
            Destroy(skyboxInstance);
            skyboxInstance = null;
        }
        if (voxelSkyActive)
        {
            RenderSettings.skybox = originalSkybox;
            if (cam != null) cam.clearFlags = previousClearFlags;
            if (createdSkyMaterial && skyMaterial != null) { Destroy(skyMaterial); skyMaterial = null; createdSkyMaterial = false; }
            voxelSkyActive = false;
        }
    }

    void Update()
    {
        float speed = Input.GetKey(fastForwardKey) ? fastForwardSpeed : 1f;
        TimeOfDay = Mathf.Repeat(TimeOfDay + Time.deltaTime * speed / (dayLengthMinutes * 60f), 1f);
        Apply();
    }

    // ------------------------------------------------------------------
    // Calcul d'un moment de la journée (fonction pure : testable, et réutilisable ailleurs)
    // ------------------------------------------------------------------

    public struct Moment
    {
        public float elevation;   // hauteur du soleil : 1 à midi, 0 à l'horizon, -1 à minuit
        public float daylight;    // lumière du ciel : nightSkyLight à minuit, 1 en plein jour
        public float sunWeight;   // 0 à 1 : présence du soleil
        public float moonWeight;  // 0 à 1 : présence de la lune
        public float sunsetGlow;  // 0 à 1 : lueur orangée près de l'horizon (aube et crépuscule)
    }

    public static Moment Evaluate(float timeOfDay, float nightSkyLight)
    {
        float e = Mathf.Sin(timeOfDay * 2f * Mathf.PI);

        Moment m;
        m.elevation = e;
        m.daylight = Mathf.Lerp(nightSkyLight, 1f, SmoothStep(-0.25f, 0.25f, e));
        m.sunWeight = SmoothStep(-0.02f, 0.12f, e);   // s'éteint juste sous l'horizon...
        m.moonWeight = SmoothStep(0.02f, 0.2f, -e);   // ...et la lune ne s'allume qu'après : jamais les deux
        m.sunsetGlow = 1f - SmoothStep(0f, 0.3f, Mathf.Abs(e - 0.05f));
        return m;
    }

    void Apply()
    {
        Moment m = Evaluate(TimeOfDay, nightSkyLight);
        Daylight = m.daylight;

        // Obscurité lue par les shaders des blocs et de l'eau (0 = jour)
        Shader.SetGlobalFloat(DarknessId, 1f - m.daylight);

        // Course du soleil : position EXACTE (pour le ciel, fluide) et axe de sa course
        Quaternion pathRotation = Quaternion.Euler(0f, sunYaw, 0f) * Quaternion.AngleAxis(sunTilt, Vector3.right);
        float exact = TimeOfDay * 2f * Mathf.PI;
        Vector3 exactSunPos = pathRotation * new Vector3(Mathf.Cos(exact), Mathf.Sin(exact), 0f);
        Shader.SetGlobalVector(SunDirId, exactSunPos);
        Shader.SetGlobalVector(SunAxisId, pathRotation * Vector3.forward);

        // Soleil ou lune : la même lumière directionnelle (par crans, pour des ombres immobiles)
        if (sun != null)
        {
            // Direction du soleil, arrondie au cran près (les ombres ne bougent qu'à chaque cran)
            float degrees = TimeOfDay * 360f;
            if (sunAngleStep > 0f) degrees = Mathf.Round(degrees / sunAngleStep) * sunAngleStep;
            float a = degrees * Mathf.Deg2Rad;
            Vector3 sunPos = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            sunPos = Quaternion.Euler(0f, sunYaw, 0f) * (Quaternion.AngleAxis(sunTilt, Vector3.right) * sunPos);

            bool day = m.elevation >= 0f;
            Vector3 lightPos = day ? sunPos : -sunPos;             // la lune est à l'opposé du soleil
            sun.transform.rotation = Quaternion.LookRotation(-lightPos);

            if (day)
            {
                sun.color = Color.Lerp(sunsetColor, noonColor, SmoothStep(0f, 0.35f, m.elevation));
                sun.intensity = sunIntensity * m.sunWeight;
            }
            else
            {
                sun.color = moonColor;
                sun.intensity = moonIntensity * m.moonWeight;
            }
        }

        // Couleur du ciel
        float dayAmount = SmoothStep(-0.25f, 0.25f, m.elevation);
        Color sky = Color.Lerp(nightSky, daySky, dayAmount);
        Color zenith = sky;
        sky = Color.Lerp(sky, sunsetSky, m.sunsetGlow * 0.8f);

        // Ciel Voxel/Sky : zénith, horizon (plus clair, orangé au lever et au coucher), étoiles la nuit
        Color horizon = Color.Lerp(Color.Lerp(nightSky, Color.Lerp(daySky, Color.white, 0.35f), dayAmount), sunsetSky, m.sunsetGlow * 0.9f);
        Shader.SetGlobalColor(ZenithId, zenith);
        Shader.SetGlobalColor(HorizonId, horizon);
        Shader.SetGlobalFloat(NightId, 1f - SmoothStep(-0.3f, 0f, m.elevation));

        if (cam != null && cam.clearFlags == CameraClearFlags.SolidColor) cam.backgroundColor = sky;
        if (RenderSettings.fog) RenderSettings.fogColor = sky;
        if (skyboxInstance != null)
            skyboxInstance.SetFloat(ExposureId, Mathf.Lerp(nightSkyboxExposure, daySkyboxExposure, SmoothStep(-0.25f, 0.25f, m.elevation)));
    }

    static float SmoothStep(float a, float b, float t)
    {
        t = Mathf.Clamp01((t - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    static Light[] FindLights()
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
#else
        return Object.FindObjectsOfType<Light>();
#endif
    }
}
