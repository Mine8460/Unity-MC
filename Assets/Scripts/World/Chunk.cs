using UnityEngine;

// La partie "Unity" d'un chunk : son GameObject, son mesh visible et son collider de raycast.
// Les DONNÉES (blocs, lumière) sont dans ChunkData, et la construction du mesh dans ChunkMesher :
// ce sont elles qui tournent sur les threads secondaires.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public partial class Chunk : MonoBehaviour
{
    // SizeX et SizeZ doivent rester des puissances de 2 (le monde utilise des décalages de bits : x >> BitsXZ)
    public const int BitsXZ = 4;
    public const int SizeX = 1 << BitsXZ;
    public const int SizeZ = 1 << BitsXZ;
    public const int MaskXZ = SizeX - 1;

    // Hauteur du monde, en blocs. 256 = comme Minecraft (changer ce nombre invalide les sauvegardes).
    public const int SizeY = 256;

    // Nombre de blocs d'un chunk
    public const int DataLength = SizeX * SizeY * SizeZ;

    // Taille d'un chunk sauvegardé : le type de chaque bloc, puis son état (niveau de l'eau...)
    public const int SaveLength = DataLength * 2;

    public ChunkData Data { get; private set; }
    public Vector2Int Coord => Data.coord;
    public int Highest => Data.highest;

    public bool IsMeshed { get; private set; }

    // true si un bloc a été changé depuis la génération / le dernier enregistrement
    public bool IsModified { get; private set; }

    // --- État du maillage en arrière-plan (géré par World, sur le thread principal) ---
    public bool MeshInFlight;   // une tâche de maillage est en cours
    public bool RemeshQueued;   // un nouveau maillage a été demandé pendant qu'une tâche tournait
    public int BuildSerial;     // numéro de la dernière construction lancée : un résultat périmé est ignoré

    // --- Collider de raycast : seulement près du joueur (le calculer est coûteux) ---
    public bool WantsCollider;
    public bool NeedsCollider => needsCollider;
    public bool HasCollider => hasCollider;

    Mesh mesh;          // ce qu'on VOIT
    Mesh colliderMesh;  // ce qu'on peut VISER
    MeshFilter meshFilter;
    MeshCollider meshCollider;
    bool needsCollider;
    bool hasCollider;
    int colliderTriangles;

    public void Init(ChunkData data, Material material, Material waterMaterial)
    {
        Data = data;

        transform.position = new Vector3(data.coord.x * SizeX, 0, data.coord.y * SizeZ);

        meshFilter = GetComponent<MeshFilter>();
        meshCollider = GetComponent<MeshCollider>();
        // Deux matériaux : les blocs (sous-mesh 0) et l'eau, transparente (sous-mesh 1)
        GetComponent<MeshRenderer>().sharedMaterials = waterMaterial != null
            ? new[] { material, waterMaterial }
            : new[] { material };

        mesh = new Mesh { name = $"Chunk {data.coord.x},{data.coord.y}" };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        colliderMesh = new Mesh { name = $"Chunk Collider {data.coord.x},{data.coord.y}" };
        colliderMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    }

    // ------------------------------------------------------------------
    // Accès aux blocs / sauvegarde
    // ------------------------------------------------------------------

    // Lecture directe dans CE chunk (coordonnées locales valides uniquement)
    public BlockType GetLocalBlock(int x, int y, int z) => Data.GetBlock(x, y, z);

    // Modifie un bloc (coordonnées locales) et marque le chunk comme modifié.
    // Ne reconstruit PAS le mesh : c'est World.SetBlock qui s'en charge.
    public byte GetLocalState(int x, int y, int z) => Data.GetState(x, y, z);

    public void SetLocalBlock(int x, int y, int z, BlockType type) => SetLocalBlock(x, y, z, type, 0);

    public void SetLocalBlock(int x, int y, int z, BlockType type, byte blockState)
    {
        Data.SetBlock(x, y, z, type, blockState);

        // Un bloc du haut a été retiré : on resserre la zone à parcourir
        if (type == BlockType.Air && y >= Data.highest) Data.RecomputeHighest();

        IsModified = true;
    }

    public void ClearModified() => IsModified = false;

    // Copie des blocs et de leurs états, pour la sauvegarde
    public byte[] ExportData()
    {
        var copy = new byte[SaveLength];
        System.Buffer.BlockCopy(Data.blocks, 0, copy, 0, DataLength);
        System.Buffer.BlockCopy(Data.state, 0, copy, DataLength, DataLength);
        return copy;
    }

    // ------------------------------------------------------------------
    // Mesh
    // ------------------------------------------------------------------

    // Envoie à Unity un mesh construit par ChunkMesher (thread principal uniquement)
    public void ApplyMesh(MeshBuffers b)
    {
        mesh.Clear();
        mesh.SetVertices(b.vertices);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(b.triangles, 0);
        mesh.SetTriangles(b.waterTriangles, 1);
        mesh.SetNormals(b.normals);
        mesh.SetUVs(0, b.uvs);
        mesh.SetColors(b.colors);
        mesh.RecalculateBounds();
        meshFilter.sharedMesh = mesh;

        colliderMesh.Clear();
        colliderMesh.SetVertices(b.colVerts);
        colliderMesh.SetTriangles(b.colTris, 0);
        colliderTriangles = b.colTris.Count;

        IsMeshed = true;
        needsCollider = true; // le collider ne correspond plus au mesh
    }

    // Active le collider de raycast (coûteux : Unity calcule sa géométrie de collision)
    public void ApplyCollider()
    {
        needsCollider = false;
        hasCollider = false;

        meshCollider.sharedMesh = null; // force la mise à jour
        if (colliderTriangles > 0)
        {
            meshCollider.sharedMesh = colliderMesh;
            hasCollider = true;
        }
    }

    // Le joueur s'est éloigné : on retire le collider (il sera recréé s'il revient)
    public void RemoveCollider()
    {
        meshCollider.sharedMesh = null;
        needsCollider = colliderTriangles > 0;
        hasCollider = false;
    }

    // Les Mesh créés par code ne sont pas libérés automatiquement : on les détruit avec le chunk
    void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (colliderMesh != null) Destroy(colliderMesh);
    }
}
