using System;
using UnityEngine;

// Un bloc, défini dans un FICHIER (.asset) au lieu du code.
// Créer : clic droit dans la fenêtre Project > Create > Voxel > Bloc.
// Les fichiers doivent être rangés dans Assets/Resources/Blocks/ (sous-dossiers permis) : le jeu les charge tous
// au lancement et assemble lui-même l'atlas de textures (plus de numéros de tuiles à gérer).
[CreateAssetMenu(menuName = "Voxel/Bloc", fileName = "NouveauBloc", order = 0)]
public class BlockDefinition : ScriptableObject
{
    [Header("Identité")]
    [Tooltip("Numéro du bloc (1 à 255), écrit dans les sauvegardes : ne le change JAMAIS une fois utilisé.\n" +
             "Pour un bloc utilisé par le code (pierre, eau, minerais...), il doit être égal à sa valeur dans l'enum BlockType.")]
    public BlockType id;
    [Tooltip("Nom affiché dans l'inventaire (vide = nom du fichier)")]
    public string displayName;

    [Header("Forme et textures")]
    public BlockShape shape = BlockShape.Cube;
    [Tooltip("Textures des côtés. Elles servent aussi pour le dessus et le dessous si ceux-ci sont vides.")]
    public FaceTextures side = new FaceTextures();
    [Tooltip("Vide = celles des côtés")]
    public FaceTextures top = new FaceTextures();
    [Tooltip("Vide = celles des côtés")]
    public FaceTextures bottom = new FaceTextures();
    [Tooltip("Éclairage par le shader : Pixel (normal), Block (plantes), FullBright (torche : ni ombre ni lumière)")]
    public LightMode lightMode = LightMode.Pixel;
    [Tooltip("Petit décalage aléatoire selon la position (herbes hautes)")]
    public bool randomOffset;

    [Header("Forme « Model » : des boîtes (en 16èmes de bloc), ou un modèle Blockbench")]
    public ModelBox[] boxes = new ModelBox[0];
    [Tooltip("Modèle Blockbench (format Java Block/Item, .json). S'il est rempli, il remplace les boîtes.")]
    public TextAsset blockbenchModel;
    [Tooltip("Textures du modèle Blockbench : le nom écrit dans le .json (ex. « anvil ») et l'image correspondante")]
    public NamedTexture[] blockbenchTextures = new NamedTexture[0];
    public ModelCollision blockbenchCollision = ModelCollision.Elements;

    [Header("Physique")]
    [Tooltip("Cache les faces des blocs voisins et bloque la lumière (cubes pleins)")]
    public bool opaque = true;
    public bool collidable = true;
    [Tooltip("Pas de face entre deux blocs identiques (vitre, feuilles, dalles)")]
    public bool cullSameType;
    [Tooltip("Un autre bloc peut prendre sa place à la pose (air, herbe haute)")]
    public bool replaceable;
    [Tooltip("Les plantes peuvent y pousser (herbe, terre)")]
    public bool isSoil;
    [Tooltip("Tombe quand plus rien ne le soutient (enclume)")]
    public bool gravity;
    [Tooltip("Ce dont le bloc a besoin pour exister (torche : un cube dessous ; plante : de la terre)")]
    public SupportRule support = SupportRule.None;
    [Tooltip("Pour le support « SolidAttached » : direction du bloc qui le soutient (torche murale : vers son mur)")]
    public Vector3Int attachDir;

    [Header("Lumière")]
    [Tooltip("Lumière émise (torche : 14)")]
    [Range(0, 15)] public int emission;
    [Tooltip("Lumière absorbée en traversant le bloc, s'il n'est pas opaque (feuilles : 1)")]
    [Range(0, 15)] public int lightFilter;

    [Header("Casse")]
    [Tooltip("Temps de casse à la main, en secondes (0 = instantané, -1 = incassable)")]
    public float breakTime = 1f;
    [Tooltip("Outil qui accélère la casse")]
    public ToolKind tool = ToolKind.None;
    [Tooltip("Niveau d'outil exigé pour qu'il lâche quelque chose (0 = aucun ; 1 bois, 2 pierre, 3 fer, 4 diamant)")]
    [Range(0, 4)] public int harvestLevel;
    [Tooltip("Ne lâche rien (feuilles, vitre, herbe haute)")]
    public bool dropsNothing;
    [Tooltip("Lâche un autre bloc (l'herbe lâche de la terre). Vide = lui-même")]
    public BlockDefinition dropBlock;
    [Tooltip("Lâche un objet à la place (minerai de charbon -> charbon). None = rien de spécial")]
    public ItemType dropItem = ItemType.None;
    [Tooltip("Nombre d'objets lâchés (0 ou 1 = un seul)")]
    [Min(0)] public int dropCount;
    [Tooltip("En plus : de 0 à ce nombre d'objets, tirés au hasard (minerai de redstone : 4 + 0 à 1)")]
    [Min(0)] public int dropCountRandom;
}

// Les textures d'une face (ou d'un groupe de faces). Seule « albedo » est obligatoire.
[Serializable]
public class FaceTextures
{
    [Tooltip("La texture du bloc (couleurs)")]
    public Texture2D albedo;
    [Tooltip("Relief (gris : blanc = surface, foncé = creux). Option du matériau des blocs.")]
    public Texture2D height;
    [Tooltip("Rouge = métal, alpha = lissé. Option du matériau des blocs.")]
    public Texture2D metallic;
    [Tooltip("Ce qui brille dans le noir. Option du matériau des blocs.")]
    public Texture2D emission;
}

// Une boîte d'un modèle, en 16èmes de bloc (comme Blockbench et Minecraft)
[Serializable]
public class ModelBox
{
    [Tooltip("Coin minimum (x, y, z), de 0 à 16")]
    public Vector3 from = Vector3.zero;
    [Tooltip("Coin maximum (x, y, z), de 0 à 16")]
    public Vector3 to = new Vector3(16, 16, 16);
    [Tooltip("Vide = texture du dessus du bloc")]
    public Texture2D top;
    [Tooltip("Vide = texture des côtés du bloc")]
    public Texture2D side;
    [Tooltip("Vide = texture du dessous du bloc")]
    public Texture2D bottom;
}

[Serializable]
public class NamedTexture
{
    public string name;
    public Texture2D texture;
}
