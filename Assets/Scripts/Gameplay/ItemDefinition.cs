using UnityEngine;

// Un objet (qui n'est pas un bloc), défini dans un FICHIER (.asset) au lieu du code.
// Créer : clic droit dans la fenêtre Project > Create > Voxel > Objet.
// Range les fichiers dans Assets/Resources/Items/ (sous-dossiers permis) : le jeu les charge tous au lancement.
[CreateAssetMenu(menuName = "Voxel/Objet", fileName = "NouvelObjet", order = 1)]
public class ItemDefinition : ScriptableObject
{
    [Header("Identité")]
    [Tooltip("Numéro de l'objet, à partir de 256 (écrit dans les sauvegardes : ne le change JAMAIS une fois utilisé).\n" +
             "Conseil : commence à 1000 pour tes propres objets. Le même numéro qu'un objet du code le remplace.")]
    public ItemType id;
    [Tooltip("Nom affiché (vide = nom du fichier)")]
    public string displayName;

    [Header("Apparence")]
    [Tooltip("Icône (PNG carré, Filter Mode = Point). Vide = icône générée avec la couleur ci-dessous. " +
             "Cette image sert aussi à fabriquer le modèle 3D tenu en main et au sol.")]
    public Texture2D icon;
    public Color32 iconColor = new Color32(200, 200, 200, 255);

    [Header("Inventaire")]
    [Range(1, 64)] public int maxStack = 64;

    [Header("Outil (laisser sur « None » si ce n'est pas un outil)")]
    public ToolKind tool = ToolKind.None;
    [Tooltip("1 = bois / or, 2 = pierre, 3 = fer, 4 = diamant (comparé au niveau exigé par le bloc)")]
    public int toolLevel = 1;
    [Tooltip("Multiplie la vitesse de casse des blocs qui lui conviennent")]
    public float toolSpeed = 2f;
    [Tooltip("Nombre de blocs cassés avant que l'outil casse (0 = pas d'usure)")]
    public int durability;

    [Header("Combat")]
    [Tooltip("Dégâts infligés aux monstres (20 = un monstre entier). 0 = ceux de la main (1). Pour une épée : tool = Sword, durabilité, et 4 à 8 de dégâts.")]
    public float attackDamage;

    [Header("Nourriture (0 = pas comestible)")]
    [Tooltip("Points de faim rendus (2 = une cuisse)")]
    public int food;
    public float saturation;

    [Header("Combustible (four)")]
    [Tooltip("Secondes de combustion dans un four (charbon : 80, bûche : 15, bâton : 5). 0 = pas un combustible.")]
    public float fuelTime;

    [Header("Pose")]
    [Tooltip("Si rempli, cet objet pose ce bloc (graines, seau...). Vide = ne se pose pas.")]
    public BlockDefinition placesBlock;
}
