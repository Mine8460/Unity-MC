using UnityEngine;

// Une recette de cuisson (four) dans un FICHIER. Créer : Project > Create > Voxel > Cuisson.
// Range-la dans Assets/Resources/Smelting/. Elle REMPLACE celle du code qui a le même objet de départ.
[CreateAssetMenu(menuName = "Voxel/Cuisson", fileName = "NouvelleCuisson", order = 3)]
public class SmeltingDefinition : ScriptableObject
{
    public ItemRef input;
    public ItemRef result;
    [Range(1, 64)] public int resultCount = 1;
    [Tooltip("Secondes de cuisson (Minecraft : 10)")]
    public float cookTime = 10f;
}
