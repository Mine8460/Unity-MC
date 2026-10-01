using UnityEngine;

// Bloc qui tombe (enclume...) : il quitte la grille, tombe comme une entité, puis se repose sur la première
// SURFACE SOLIDE qu'il rencontre. Si la case où il s'arrête n'est pas remplaçable (torche, dalle, tabouret...),
// il ne peut pas redevenir un bloc : il devient un objet.
public class FallingBlock : MonoBehaviour
{
    [SerializeField] float gravity = 16f;   // blocs/s² (Minecraft : 0.04 bloc/tick²)
    [SerializeField] float maxSpeed = 40f;  // blocs/s

    World world;
    BlockType type;
    int cellX, cellZ;
    float speed;   // vers le bas, en blocs/s
    bool done;

    // Chunk dans lequel tombe le bloc (sa colonne ne change jamais)
    public Vector2Int ChunkCoord => new Vector2Int(
        Mathf.FloorToInt(cellX / (float)Chunk.SizeX),
        Mathf.FloorToInt(cellZ / (float)Chunk.SizeZ));

    // `cell` : la case que le bloc vient de quitter. Le GameObject doit déjà être à l'angle de cette case.
    public void Init(World world, BlockType type, Vector3Int cell)
    {
        this.world = world;
        this.type = type;
        cellX = cell.x;
        cellZ = cell.z;
    }

    void Update()
    {
        if (done) return;
        if (!world.IsLoaded(cellX, cellZ)) return; // chunk déchargé : on attend

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        speed = Mathf.Min(speed + gravity * dt, maxSpeed);

        float oldBottom = transform.position.y;
        float newBottom = oldBottom - speed * dt;

        if (FindSurface(oldBottom, newBottom, out float surface))
        {
            transform.position = new Vector3(cellX, surface, cellZ);
            Land(surface);
            return;
        }

        transform.position = new Vector3(cellX, newBottom, cellZ);
    }

    // Première surface solide que la base du bloc traverse entre oldBottom et newBottom.
    // Les blocs sans collision (herbe, torche, feuilles) sont traversés. Le fond du monde est une surface.
    bool FindSurface(float oldBottom, float newBottom, out float surface)
    {
        int top = Mathf.FloorToInt(oldBottom + 1e-4f);
        int bottom = Mathf.FloorToInt(newBottom);

        for (int cy = top; cy >= bottom && cy >= 0; cy--)
        {
            float height = BlockDatabase.SupportHeight(world.GetBlock(cellX, cy, cellZ));
            if (height <= 0f) continue;

            float s = cy + height;
            if (s <= oldBottom + 1e-4f && s >= newBottom - 1e-4f)
            {
                surface = s;
                return true;
            }
        }

        if (newBottom <= 0f)
        {
            surface = 0f;
            return true;
        }

        surface = 0f;
        return false;
    }

    // Le bloc s'arrête sur une surface : la case d'arrivée est celle où se trouve sa base.
    // Sur une dalle (surface à 0,5), c'est la case de la dalle elle-même : non remplaçable, donc un objet.
    void Land(float surface)
    {
        if (done) return;
        done = true;

        int cellY = Mathf.FloorToInt(surface + 1e-4f);
        world.LandFallingBlock(cellX, cellY, cellZ, type);
        world.UnregisterFalling(this);
        Destroy(gameObject);
    }

    // Pose le bloc immédiatement (déchargement du chunk, fermeture du jeu) : il n'est jamais perdu
    public void SettleNow()
    {
        if (done) return;

        float bottom = transform.position.y;
        float surface = 0f;

        for (int cy = Mathf.FloorToInt(bottom + 1e-4f); cy >= 0; cy--)
        {
            float height = BlockDatabase.SupportHeight(world.GetBlock(cellX, cy, cellZ));
            if (height > 0f && cy + height <= bottom + 1e-4f)
            {
                surface = cy + height;
                break;
            }
        }

        Land(surface);
    }
}
