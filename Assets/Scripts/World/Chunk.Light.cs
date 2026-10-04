// Accès à la lumière d'un chunk (les données sont dans ChunkData).
// (Partie de la classe Chunk.)
public partial class Chunk
{
    public int GetBlockLight(int x, int y, int z) => Data.GetBlockLight(x, y, z);
    public int GetSkyLight(int x, int y, int z) => Data.GetSkyLight(x, y, z);

    public void SetBlockLight(int x, int y, int z, int level) => Data.SetBlockLight(x, y, z, level);
    public void SetSkyLight(int x, int y, int z, int level) => Data.SetSkyLight(x, y, z, level);
}
