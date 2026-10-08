using UnityEngine;

// Objets au sol, casse de blocs qui lâche des objets, atterrissage des blocs qui tombent.
// (Partie de la classe World.)
public partial class World
{
    [Header("Objets")]
    [Tooltip("Inventaire du joueur (trouvé automatiquement s'il est vide)")]
    [SerializeField] Inventory playerInventory;

    bool inventorySearched;

    public Material ChunkMaterial => chunkMaterial;
    public Material TranslucentMaterial => translucentMaterial != null ? translucentMaterial : chunkMaterial;

    // L'atlas de textures (pour dessiner les icônes de l'interface)
    public Texture AtlasTexture => chunkMaterial != null ? chunkMaterial.GetTexture("_BaseMap") : null;

    public Inventory PlayerInventory
    {
        get
        {
            if (playerInventory == null && !inventorySearched)
            {
                inventorySearched = true;
#if UNITY_2023_1_OR_NEWER
                playerInventory = FindFirstObjectByType<Inventory>();
#else
                playerInventory = FindObjectOfType<Inventory>();
#endif
            }
            return playerInventory;
        }
    }

    // Mesh d'un bloc seul (objets au sol, blocs qui tombent, icônes). Partagé entre toutes les entités.
    public Mesh GetBlockMesh(BlockType type) => GetFallingMesh(type);

    // Fait apparaître un objet (bloc, outil, lingot...). `position` = centre du bas de sa boîte de collision.
    public ItemEntity DropItem(ItemStack stack, Vector3 position, Vector3 velocity, float pickupDelay = 0.5f)
    {
        if (stack.IsEmpty) return null;

        var go = new GameObject("Item " + stack.type);
        go.transform.SetParent(transform);
        go.transform.position = position;

        var item = go.AddComponent<ItemEntity>();
        item.Init(this, stack, velocity, pickupDelay);
        return item;
    }

    public ItemEntity DropItem(ItemType type, int count, Vector3 position, Vector3 velocity, float pickupDelay = 0.5f) =>
        DropItem(new ItemStack(type, count), position, velocity, pickupDelay);

    public ItemEntity DropItem(BlockType type, int count, Vector3 position, Vector3 velocity, float pickupDelay = 0.5f) =>
        DropItem(new ItemStack(type, count), position, velocity, pickupDelay);

    // Matériau des objets plats au sol (outils, lingots...) : une copie du matériau des blocs, sans ses options
    // (relief, metallic...) qui supposent l'atlas. La texture de chaque objet est donnée par son renderer.
    Material flatItemMaterial;
    public Material FlatItemMaterial
    {
        get
        {
            if (flatItemMaterial == null && chunkMaterial != null)
            {
                flatItemMaterial = new Material(chunkMaterial) { name = "Flat items" };
                flatItemMaterial.DisableKeyword("_PARALLAXMAP");
                flatItemMaterial.DisableKeyword("_METALLICMAP");
                flatItemMaterial.DisableKeyword("_EMISSION");
            }
            return flatItemMaterial;
        }
    }

    // Lâche l'objet d'un bloc cassé (selon ses règles : l'herbe donne de la terre, les feuilles rien...)
    public void DropBlockItem(BlockType type, Vector3 position)
    {
        BlockInfo info = BlockDatabase.Get(type);
        // Les feuilles lâchent parfois une pomme
        if (type == BlockType.Leaves && Random.value < 0.05f)
            DropItem(ItemType.Apple, 1, position, new Vector3(Random.Range(-1f, 1f), 3f, Random.Range(-1f, 1f)));

        if (info.dropsNothing) return;

        // L'objet lâché : un objet précis (minerai de charbon -> charbon), sinon un bloc (l'herbe -> la terre), sinon lui-même
        ItemType item = info.dropItem != ItemType.None
            ? info.dropItem
            : ItemDatabase.FromBlock(info.dropOverride != BlockType.Air ? info.dropOverride : type);

        var velocity = new Vector3(Random.Range(-1f, 1f), Random.Range(2.5f, 3.5f), Random.Range(-1f, 1f));
        int count = Mathf.Max(1, (int)info.dropCount);
        if (info.dropCountRandom > 0) count += Random.Range(0, info.dropCountRandom + 1);
        DropItem(item, count, position, velocity);
    }

    // Casse un bloc : il disparaît et lâche son objet.
    // À UTILISER à la place de SetBlock(..., Air) quand le JOUEUR casse un bloc.
    public bool BreakBlock(int x, int y, int z, bool drop = true)
    {
        BlockType type = GetBlock(x, y, z);
        if (!BlockDatabase.Get(type).hasMesh) return false;
        if (!SetBlock(x, y, z, BlockType.Air)) return false;

        if (FurnaceIds.IsFurnace(type)) DropFurnaceContents(x, y, z); // le contenu du four tombe toujours

        if (drop) DropBlockItem(type, new Vector3(x + 0.5f, y + 0.1f, z + 0.5f));
        return true;
    }

    // Test : clic droit sur le composant World (pendant le jeu) pour lâcher une pierre devant le joueur.
    // Sert à vérifier que les objets au sol fonctionnent, indépendamment de ton code de casse.
    [ContextMenu("Test : lâcher une pierre devant le joueur")]
    void DebugDropStone()
    {
        Transform t = PlayerInventory != null ? PlayerInventory.transform : transform;
        DropItem(BlockType.Stone, 1, t.position + t.forward * 2f + Vector3.up * 2f, Vector3.zero, 0.5f);
    }

    // Un bloc qui tombait arrive à destination. Si la case d'arrivée n'est pas remplaçable
    // (torche, dalle, tabouret...), il ne peut pas se poser : il devient un objet.
    public void LandFallingBlock(int x, int y, int z, BlockType type)
    {
        if (y >= 0 && y < Chunk.SizeY && BlockDatabase.Get(GetBlock(x, y, z)).replaceable)
        {
            SetBlock(x, y, z, type);
            return;
        }

        DropBlockItem(type, new Vector3(x + 0.5f, y + 0.1f, z + 0.5f));
    }
}
