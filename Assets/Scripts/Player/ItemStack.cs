using System;

// Une pile d'objets : un objet (bloc, outil, lingot...), une quantité, et l'usure si c'est un outil.
[Serializable]
public struct ItemStack
{
    public ItemType type;
    public int count;
    public int damage;   // usure d'un outil (0 = neuf ; il casse quand elle atteint sa durabilité)

    public ItemStack(ItemType type, int count, int damage = 0)
    {
        this.type = type;
        this.count = count;
        this.damage = damage;
    }

    // Pile d'un bloc (compatible avec le code qui ne manipulait que des blocs)
    public ItemStack(BlockType block, int count) : this(ItemDatabase.FromBlock(block), count) { }

    public bool IsEmpty => count <= 0 || type == ItemType.None;

    // Est-ce un bloc (qu'on peut poser) ? Et lequel ?
    public bool IsBlock => !IsEmpty && ItemDatabase.IsBlock(type);
    public BlockType Block => ItemDatabase.ToBlock(type);

    // Deux piles peuvent-elles fusionner ? (même objet, même usure ; les outils ne s'empilent pas : maxStack = 1)
    public bool CanStackWith(ItemStack other) => type == other.type && damage == other.damage;
}
