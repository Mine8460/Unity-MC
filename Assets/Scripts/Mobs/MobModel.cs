using System.Collections.Generic;
using UnityEngine;

// Fabrique le modèle d'un monstre (boîtes + texture, ou prefab) et le fait bouger.
public class MobModel
{
    class PartInstance
    {
        public Transform t;
        public MobPart def;
        public Quaternion rest;
    }

    readonly List<PartInstance> parts = new List<PartInstance>();
    readonly List<Renderer> renderers = new List<Renderer>();
    Animator animator;
    HashSet<string> animParams;
    float phase;
    float headYaw;
    MaterialPropertyBlock block;

    static readonly Dictionary<MobDefinition, Material> materials = new Dictionary<MobDefinition, Material>();

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    public static MobModel Build(MobDefinition def, Transform root)
    {
        var m = new MobModel();
        if (def.prefab != null)
        {
            GameObject inst = Object.Instantiate(def.prefab, root);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.Euler(0, def.modelYawOffset, 0);
            SetLayer(inst, 2);
            foreach (Collider c in inst.GetComponentsInChildren<Collider>()) Object.Destroy(c);
            m.animator = inst.GetComponentInChildren<Animator>();
            if (m.animator != null)
            {
                m.animParams = new HashSet<string>();
                foreach (AnimatorControllerParameter p in m.animator.parameters) m.animParams.Add(p.name);
            }
            m.renderers.AddRange(inst.GetComponentsInChildren<Renderer>());
            return m;
        }

        MobPart[] src = def.parts != null && def.parts.Length > 0 ? def.parts : MobDatabase.HumanoidParts();
        Vector2Int tex = def.textureSize.x > 0 && def.textureSize.y > 0 ? def.textureSize : new Vector2Int(64, 32);
        Material mat = GetMaterial(def);

        var modelRoot = new GameObject("Model").transform;
        modelRoot.SetParent(root, false);
        modelRoot.localScale = Vector3.one * def.modelScale;
        modelRoot.localRotation = Quaternion.Euler(0, def.modelYawOffset, 0);
        SetLayer(modelRoot.gameObject, 2);

        foreach (MobPart p in src)
        {
            var go = new GameObject(string.IsNullOrEmpty(p.name) ? "part" : p.name);
            go.layer = 2;
            go.transform.SetParent(modelRoot, false);
            go.transform.localPosition = p.pivot / 16f;
            Quaternion rest = Quaternion.Euler(p.restRotation);
            go.transform.localRotation = rest;

            go.AddComponent<MeshFilter>().sharedMesh = BuildBox(p, tex);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            m.renderers.Add(r);
            m.parts.Add(new PartInstance { t = go.transform, def = p, rest = rest });
        }
        return m;
    }

    static void SetLayer(GameObject go, int layer)
    {
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    static Material GetMaterial(MobDefinition def)
    {
        Material mat;
        if (materials.TryGetValue(def, out mat) && mat != null) return mat;

        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        mat = new Material(sh) { name = "Monstre " + def.name };
        Texture2D skin = def.skin != null ? def.skin : MobDatabase.DefaultSkin();
        skin.filterMode = FilterMode.Point;
        mat.mainTexture = skin;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", skin);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        mat.EnableKeyword("_EMISSION");
        materials[def] = mat;
        return mat;
    }

    // Boîte avec la disposition de texture de Minecraft :
    //   [haut] [bas]
    //   [droite] [avant] [gauche] [arrière]
    static Mesh BuildBox(MobPart p, Vector2Int tex)
    {
        Vector3 a = p.boxMin / 16f;
        Vector3 b = (p.boxMin + p.size) / 16f;
        float w = p.size.x, h = p.size.y, d = p.size.z;
        float u = p.uv.x, v = p.uv.y;

        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var norms = new List<Vector3>();
        var tris = new List<int>();

        // coins : BL, TL, TR, BR vus de l'extérieur
        // droite du monstre = +X, avant = +Z
        Face(verts, uvs, norms, tris, tex, p.mirror,
             new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, a.y, b.z), Vector3.right,
             p.mirror ? u + d + w : u, v + d, d, h);                         // +X (droite)
        Face(verts, uvs, norms, tris, tex, p.mirror,
             new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, b.y, a.z), new Vector3(a.x, a.y, a.z), Vector3.left,
             p.mirror ? u : u + d + w, v + d, d, h);                         // -X (gauche)
        Face(verts, uvs, norms, tris, tex, p.mirror,
             new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, a.y, b.z), Vector3.forward,
             u + d, v + d, w, h);                                            // +Z (avant)
        Face(verts, uvs, norms, tris, tex, p.mirror,
             new Vector3(a.x, a.y, a.z), new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, a.y, a.z), Vector3.back,
             u + 2 * d + w, v + d, w, h);                                    // -Z (arrière)
        Face(verts, uvs, norms, tris, tex, p.mirror,
             new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z), new Vector3(a.x, b.y, a.z), new Vector3(a.x, b.y, b.z), Vector3.up,
             u + d, v, w, d);                                                // haut
        Face(verts, uvs, norms, tris, tex, p.mirror,
             new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z), new Vector3(a.x, a.y, a.z), Vector3.down,
             u + d + w, v, w, d);                                            // bas

        var mesh = new Mesh { name = "Mob " + p.name };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetNormals(norms);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    static void Face(List<Vector3> verts, List<Vector2> uvs, List<Vector3> norms, List<int> tris, Vector2Int tex, bool mirror,
                     Vector3 bl, Vector3 tl, Vector3 tr, Vector3 br, Vector3 normal, float ux, float uy, float uw, float uh)
    {
        int s = verts.Count;
        verts.Add(bl); verts.Add(tl); verts.Add(tr); verts.Add(br);
        for (int i = 0; i < 4; i++) norms.Add(normal);

        // Un demi-pixel de marge évite les liserés d'un pixel voisin
        float e = 0.01f;
        float x0 = (ux + e) / tex.x, x1 = (ux + uw - e) / tex.x;
        float yTop = 1f - (uy + e) / tex.y, yBot = 1f - (uy + uh - e) / tex.y;
        if (mirror) { float t = x0; x0 = x1; x1 = t; }
        uvs.Add(new Vector2(x0, yBot)); uvs.Add(new Vector2(x0, yTop)); uvs.Add(new Vector2(x1, yTop)); uvs.Add(new Vector2(x1, yBot));

        tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
        tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
    }

    // ------------------------------------------------------------------
    // Animation
    // ------------------------------------------------------------------

    // speed01 : 0 à l'arrêt, 1 à pleine vitesse. lookYaw : angle (degrés) entre l'avant du corps et le joueur.
    public void Animate(float dt, float flatSpeed, float speed01, float lookYaw, bool hasLook)
    {
        if (animator != null)
        {
            if (animParams != null && animParams.Contains("Speed")) animator.SetFloat("Speed", speed01);
            return;
        }

        phase += flatSpeed * dt * 3.2f;
        float swing = Mathf.Sin(phase) * speed01;
        headYaw = Mathf.Lerp(headYaw, hasLook ? Mathf.Clamp(lookYaw, -50f, 50f) : 0f, 8f * dt);

        for (int i = 0; i < parts.Count; i++)
        {
            PartInstance pi = parts[i];
            float amt = pi.def.animAmount;
            Quaternion q = Quaternion.identity;
            switch (pi.def.anim)
            {
                case PartAnim.SwingA: q = Quaternion.Euler(swing * amt, 0, 0); break;
                case PartAnim.SwingB: q = Quaternion.Euler(-swing * amt, 0, 0); break;
                case PartAnim.ArmsForwardA: q = Quaternion.Euler(-90f + swing * amt * 0.2f, 0, 0); break;
                case PartAnim.ArmsForwardB: q = Quaternion.Euler(-90f - swing * amt * 0.2f, 0, 0); break;
                case PartAnim.HeadLook: q = Quaternion.Euler(0, headYaw, 0); break;
                case PartAnim.Wobble: q = Quaternion.Euler(0, 0, Mathf.Sin(Time.time * 6f + i) * amt); break;
            }
            pi.t.localRotation = pi.def.anim == PartAnim.HeadLook ? q * pi.rest : pi.rest * q;
        }
    }

    public void Trigger(string name)
    {
        if (animator != null && animParams != null && animParams.Contains(name)) animator.SetTrigger(name);
    }

    // Flash rouge quand le monstre est touché
    public void SetHurt(bool hurt)
    {
        if (block == null) block = new MaterialPropertyBlock();
        block.SetColor("_EmissionColor", hurt ? new Color(0.8f, 0f, 0f) : Color.black);
        for (int i = 0; i < renderers.Count; i++)
            if (renderers[i] != null) renderers[i].SetPropertyBlock(block);
    }
}
