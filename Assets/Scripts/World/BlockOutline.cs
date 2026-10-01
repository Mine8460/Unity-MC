using System.Collections.Generic;
using UnityEngine;

// Contour du bloc visé, comme dans Minecraft. Il suit la VRAIE forme du bloc
// (demi-dalle, torche, enclume, plante...) et pas seulement un cube.
//
// Utilisation : appelle Show(...) chaque frame où ton raycast touche un bloc, et Hide() sinon.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class BlockOutline : MonoBehaviour
{
    [SerializeField] World world;

    [Tooltip("Optionnel. Sans matériau, un matériau est créé automatiquement à partir du shader Voxel/BlockOutline.")]
    [SerializeField] Material material;

    [Tooltip("Utilisée seulement si le matériau est créé automatiquement")]
    [SerializeField] Color color = new Color(0f, 0f, 0f, 1f);

    [Tooltip("Épaisseur des traits, en blocs")]
    [SerializeField, Range(0.002f, 0.05f)] float thickness = 0.012f;

    [Tooltip("Les traits sont décalés vers l'extérieur pour ne pas se confondre avec les faces du bloc")]
    [SerializeField, Range(0f, 0.02f)] float padding = 0.002f;

    static readonly Box[] FullBoxes = { new Box(Vector3.zero, Vector3.one) };
    static readonly Box[] PlantBoxes = { new Box(new Vector3(0.1f, 0f, 0.1f), new Vector3(0.7f, 0.6f, 0.7f)) };

    // Les 6 faces d'un prisme (8 sommets : le bit 0 = x, le bit 1 = y, le bit 2 = z)
    static readonly int[] PrismTriangles =
    {
        0, 2, 1,  1, 2, 3,   // z min
        4, 5, 6,  5, 7, 6,   // z max
        0, 1, 4,  1, 5, 4,   // y min
        2, 6, 3,  3, 6, 7,   // y max
        0, 4, 2,  2, 4, 6,   // x min
        1, 3, 5,  3, 7, 5,   // x max
    };

    MeshRenderer meshRenderer;
    Mesh mesh;
    Material createdMaterial;

    bool visible;
    Vector3Int block;
    BlockType shownType;

    void Awake()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        mesh = new Mesh { name = "Block outline" };
        GetComponent<MeshFilter>().sharedMesh = mesh;

        if (material == null)
        {
            Shader shader = Shader.Find("Voxel/BlockOutline");
            if (shader == null)
            {
                Debug.LogError("BlockOutline : shader \"Voxel/BlockOutline\" introuvable. " +
                               "Ajoute BlockOutline.shader au projet ou assigne un matériau.", this);
                enabled = false;
                return;
            }

            createdMaterial = new Material(shader);
            createdMaterial.SetColor("_Color", color);
            material = createdMaterial;
        }

        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.enabled = false;
    }

    void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (createdMaterial != null) Destroy(createdMaterial);
    }

    // Affiche le contour autour du bloc donné (coordonnées MONDE)
    public void Show(Vector3Int blockPos)
    {
        if (!enabled || world == null) return;

        BlockType type = world.GetBlock(blockPos.x, blockPos.y, blockPos.z);
        BlockInfo info = BlockDatabase.Get(type);
        if (!info.hasMesh) { Hide(); return; }

        if (visible && blockPos == block && type == shownType) return; // rien à refaire

        // Même type de bloc sans décalage aléatoire : la forme est identique, on déplace seulement le contour
        bool rebuild = !visible || type != shownType || info.randomOffset;

        block = blockPos;
        shownType = type;
        visible = true;

        if (rebuild) Rebuild(info);

        transform.position = new Vector3(blockPos.x, blockPos.y, blockPos.z);
        meshRenderer.enabled = true;
    }

    // Raccourci pour un Physics.Raycast : retrouve le bloc touché d'après le point et la normale
    public void ShowFromHit(Vector3 hitPoint, Vector3 hitNormal)
    {
        Show(Vector3Int.FloorToInt(hitPoint - hitNormal * 0.01f));
    }

    public void Hide()
    {
        visible = false;
        if (meshRenderer != null) meshRenderer.enabled = false;
    }

    void LateUpdate()
    {
        // Le bloc visé a changé (cassé, remplacé) : on met le contour à jour ou on le cache
        if (visible && world.GetBlock(block.x, block.y, block.z) != shownType)
            Show(block);
    }

    // ------------------------------------------------------------------
    // Construction du mesh
    // ------------------------------------------------------------------

    // Boîtes qui forment la forme du bloc (coordonnées de bloc, 0..1)
    static Box[] GetBoxes(BlockInfo info, Vector3Int pos, out Vector3 offset)
    {
        offset = Vector3.zero;

        switch (info.shape)
        {
            case BlockShape.Cube:
                return FullBoxes;

            case BlockShape.Cross:
                if (info.randomOffset) offset = Chunk.PlantOffsetAt(pos.x, pos.z);
                return PlantBoxes;

            default:
                if (info.elements != null && info.elements.Length > 0)
                {
                    var boxes = new Box[info.elements.Length];
                    for (int i = 0; i < boxes.Length; i++)
                        boxes[i] = new Box(info.elements[i].min, info.elements[i].max);
                    return boxes;
                }

                // Modèle importé de Blockbench : ses boîtes de collision, sinon le bloc entier
                return info.collisionBoxes ?? FullBoxes;
        }
    }

    void Rebuild(BlockInfo info)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        Box[] boxes = GetBoxes(info, block, out Vector3 offset);
        Vector3 pad = Vector3.one * padding;

        foreach (Box b in boxes)
            AddBoxEdges(verts, tris, b.min + offset - pad, b.max + offset + pad);

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
    }

    // Les 12 arêtes d'une boîte, chacune sous forme d'un fin prisme
    void AddBoxEdges(List<Vector3> verts, List<int> tris, Vector3 min, Vector3 max)
    {
        float h = thickness * 0.5f;

        for (int i = 0; i < 4; i++)
        {
            // 4 arêtes le long de X
            float y = (i & 1) == 0 ? min.y : max.y;
            float z = (i & 2) == 0 ? min.z : max.z;
            AddPrism(verts, tris, new Vector3(min.x - h, y - h, z - h), new Vector3(max.x + h, y + h, z + h));

            // 4 arêtes le long de Y
            float x = (i & 1) == 0 ? min.x : max.x;
            AddPrism(verts, tris, new Vector3(x - h, min.y - h, z - h), new Vector3(x + h, max.y + h, z + h));

            // 4 arêtes le long de Z
            y = (i & 2) == 0 ? min.y : max.y;
            AddPrism(verts, tris, new Vector3(x - h, y - h, min.z - h), new Vector3(x + h, y + h, max.z + h));
        }
    }

    // Le shader n'élimine aucune face (Cull Off) : le sens des triangles n'a pas d'importance
    static void AddPrism(List<Vector3> verts, List<int> tris, Vector3 min, Vector3 max)
    {
        int start = verts.Count;

        for (int i = 0; i < 8; i++)
        {
            verts.Add(new Vector3(
                (i & 1) == 0 ? min.x : max.x,
                (i & 2) == 0 ? min.y : max.y,
                (i & 4) == 0 ? min.z : max.z));
        }

        for (int i = 0; i < PrismTriangles.Length; i++)
            tris.Add(start + PrismTriangles[i]);
    }
}
