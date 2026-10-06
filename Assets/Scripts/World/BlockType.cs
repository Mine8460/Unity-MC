// Numéros des blocs UTILISÉS PAR LE CODE (génération, règles, recettes...).
// Chaque bloc est défini par un fichier Assets/Resources/Blocks/*.asset (BlockDefinition) dont le champ « Id »
// doit être égal à sa valeur ici. Un nouveau bloc dont le code n'a pas besoin n'a PAS besoin d'être ici.
// ATTENTION : ne change jamais les valeurs existantes (elles sont écrites dans les sauvegardes). Ajoute à la fin.
public enum BlockType : byte
{
    Air = 0,
    Bedrock,   // bloc indestructible (ne peut pas être placé)
    Grass,
    Dirt,
    Stone,
    Log,
    Leaves,
    Glass,
    TallGrass,
    StoneSlab,
    Anvil,
    AnvilRotated,
    Torch,
    WallTorchNorth, WallTorchSouth, WallTorchEast, WallTorchWest,
    Sand,
    StoneSlabTop,
    StoneDoubleSlab,
    Water,     // NOUVEAU : toujours à la fin, pour ne pas décaler les blocs des sauvegardes
    CoalOre,
    IronOre,
    GoldOre,
    DiamondOre,
    Planks,
    CraftingTable,

    // ----- Redstone : numéros FIXES à partir de 64 (ne les change jamais : ils sont écrits dans les sauvegardes) -----
    RedstoneOre = 64,
    RedstoneBlock = 65,
    RedstoneDust = 66,
    RedstoneLamp = 67,
    RedstoneLampLit = 68,
    // Torches de redstone allumées : au sol, puis contre un mur au nord (+Z), sud (-Z), est (+X), ouest (-X)
    RedstoneTorch = 69, RedstoneWallTorchNorth, RedstoneWallTorchSouth, RedstoneWallTorchEast, RedstoneWallTorchWest,
    RedstoneTorchUnlit = 74, RedstoneWallTorchUnlitNorth, RedstoneWallTorchUnlitSouth, RedstoneWallTorchUnlitEast, RedstoneWallTorchUnlitWest,
    Lever = 79, LeverNorth, LeverSouth, LeverEast, LeverWest,
    LeverOn = 84, LeverOnNorth, LeverOnSouth, LeverOnEast, LeverOnWest,
    StoneButton = 89, StoneButtonNorth, StoneButtonSouth, StoneButtonEast, StoneButtonWest,
    StoneButtonPressed = 94, StoneButtonPressedNorth, StoneButtonPressedSouth, StoneButtonPressedEast, StoneButtonPressedWest,
    Repeater = 99,
}
