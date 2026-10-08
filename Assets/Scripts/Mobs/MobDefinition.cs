using System;
using UnityEngine;

public enum MobBehavior { Hostile, Passive }

// Animation d'une pièce du modèle en boîtes (selon la marche du monstre)
public enum PartAnim
{
    None,
    SwingA,        // balance d'avant en arrière (jambe gauche, bras...)
    SwingB,        // pareil, en opposition de phase (jambe droite...)
    ArmsForwardA,  // bras tendu devant (zombie) avec un léger balancement
    ArmsForwardB,
    HeadLook,      // la tête suit le joueur
    Wobble,        // oscille en continu sur le côté (ailes, oreilles, queue...)
}

// Une pièce (une boîte) d'un modèle, à la manière des modèles de Minecraft. Unités : pixels (1 bloc = 16).
[Serializable]
public class MobPart
{
    public string name = "part";
    [Tooltip("Point d'articulation (la pièce tourne autour). x : gauche/droite (+x = droite du monstre), y : hauteur depuis les pieds, z : avant (+z)")]
    public Vector3 pivot;
    [Tooltip("Coin de la boîte, relatif au pivot")]
    public Vector3 boxMin = new Vector3(-4, 0, -4);
    [Tooltip("Largeur (x), hauteur (y), profondeur (z) de la boîte")]
    public Vector3 size = new Vector3(8, 8, 8);
    [Tooltip("Position de la boîte dans l'image (pixels, depuis le coin HAUT-gauche), disposition de Minecraft")]
    public Vector2Int uv;
    [Tooltip("Inverse la texture (membres gauches qui réutilisent la texture des droits)")]
    public bool mirror;
    [Tooltip("Rotation au repos (degrés)")]
    public Vector3 restRotation;
    public PartAnim anim = PartAnim.None;
    [Tooltip("Amplitude de l'animation (degrés)")]
    public float animAmount = 45f;
}

[Serializable]
public class MobDrop
{
    [Tooltip("Un objet (laisser vide si c'est un bloc)")]
    public ItemDefinition item;
    [Tooltip("Un bloc (laisser vide si c'est un objet)")]
    public BlockDefinition block;
    public int min = 1, max = 1;
    [Range(0f, 1f)] public float chance = 1f;
}

// Un monstre ou un animal, défini dans un FICHIER (.asset).
// Créer : clic droit dans Project > Create > Voxel > Monstre. Range-le dans Assets/Resources/Mobs/.
[CreateAssetMenu(menuName = "Voxel/Monstre", fileName = "NouveauMonstre", order = 3)]
public class MobDefinition : ScriptableObject
{
    [Header("Identité")]
    public string displayName;
    public MobBehavior behavior = MobBehavior.Hostile;

    [Header("Corps")]
    public float health = 20f;
    public float speed = 3f;
    [Tooltip("Largeur et hauteur de la boîte de collision (en blocs)")]
    public float width = 0.6f, height = 1.95f;
    public float jumpSpeed = 8f;

    [Header("Combat (monstre hostile)")]
    public float attackDamage = 3f;
    public float attackRange = 1.3f;
    public float attackCooldown = 1f;
    [Tooltip("Distance à laquelle il repère le joueur")]
    public float followRange = 35f;
    [Tooltip("Brûle en plein jour à l'air libre")]
    public bool burnInDaylight = true;

    [Header("Apparition")]
    [Tooltip("Plus le nombre est grand, plus il apparaît souvent par rapport aux autres. 0 = n'apparaît jamais tout seul.")]
    public float spawnWeight = 10f;
    [Tooltip("Nombre maximum de ce monstre dans le monde")]
    public int maxCount = 30;
    public int groupMin = 1, groupMax = 4;
    [Tooltip("Lumière (0 à 15) de la case où il apparaît. Monstres : 0 à 0. Animaux : 9 à 15.")]
    [Range(0, 15)] public int minLight = 0;
    [Range(0, 15)] public int maxLight = 0;
    [Tooltip("Blocs sur lesquels il peut apparaître (vide = n'importe quel bloc plein)")]
    public BlockDefinition[] spawnOnBlocks = new BlockDefinition[0];

    [Header("Modèle 1 : boîtes + texture (comme Minecraft)")]
    [Tooltip("Image complète du monstre (Filter Mode = Point). Vide = peau verte générée.")]
    public Texture2D skin;
    [Tooltip("Taille de l'image en pixels (64 x 32 pour un zombie de Minecraft, 64 x 64 pour d'autres)")]
    public Vector2Int textureSize = new Vector2Int(64, 32);
    [Tooltip("Les pièces du modèle. Vide = silhouette humaine (tête, corps, bras, jambes) avec la disposition de texture de Minecraft.")]
    public MobPart[] parts = new MobPart[0];
    public float modelScale = 1f;
    [Tooltip("Tourne le modèle sur lui-même (degrés). Le jeu suppose que l'AVANT du modèle est du côté +Z. " +
             "Si ton modèle marche à reculons, mets 180 (c'est la valeur par défaut). Le zombie d'exemple utilise 0.")]
    public float modelYawOffset = 180f;

    [Header("Modèle 2 : prefab (modèle 3D + animations)")]
    [Tooltip("S'il est rempli, il remplace les boîtes. Si le prefab a un Animator : paramètre « Speed » (float, 0 à 1), " +
             "déclencheurs « Attack », « Hurt » et « Die » (tous facultatifs). Le prefab regarde vers +Z, pieds à l'origine.")]
    public GameObject prefab;

    [Header("Butin")]
    public MobDrop[] drops = new MobDrop[0];
}
