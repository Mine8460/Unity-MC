using System;

// Une pile d'objets : un type de bloc et une quantité.
// (Pour l'instant, tous les objets sont des blocs.)
[Serializable]
public struct ItemStack
{
    public BlockType type;
    public int count;

    public ItemStack(BlockType type, int count)
    {
        this.type = type;
        this.count = count;
    }

    public bool IsEmpty => count <= 0 || type == BlockType.Air;
}
