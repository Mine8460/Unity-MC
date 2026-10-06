using System;
using UnityEngine;

// Réglages de génération du monde. Copiés dans chaque tâche : modifier l'Inspector pendant le jeu
// ne peut donc pas perturber une génération en cours.
public struct TerrainSettings
{
    public int seed;
    public int baseHeight;        // hauteur moyenne des plaines (en blocs)
    public int seaLevel;          // les creux plus bas se remplissent d'eau (0 = pas d'eau)
    public float hillHeight;      // hauteur des collines
    public float mountainHeight;  // hauteur des montagnes au-dessus des plaines
    public float scale;           // taille des reliefs : 2 = deux fois plus étalés
    public bool caves;
    public bool ores;             // filons de minerais dans la pierre
    public float caveDensity;     // 0 à 1
    public float treeDensity;     // 0 à 1 (0 = aucun arbre)
    public bool showcase;         // quelques blocs de démonstration près de l'origine
}

// Génération du monde. 100 % thread-safe : une fonction pure de (position, réglages), sans rien d'Unity
// (ni Mathf.PerlinNoise, ni Random). Le même bloc donne donc toujours le même résultat, sur n'importe quel
// thread, et deux chunks voisins se raccordent parfaitement (un arbre à cheval sur deux chunks, par exemple).
//
// Les étapes, pour chaque chunk :
//   1. le RELIEF : une hauteur de surface par colonne (plaines, collines, montagnes) ;
//   2. les COUCHES : herbe, terre, pierre ; la roche affleure sur les pentes raides et en altitude ;
//      sable sur les plages et au fond de l'eau ; bedrock tout en bas ; eau jusqu'au niveau de la mer ;
//   3. les GROTTES : des tunnels qui serpentent et de grandes cavernes en profondeur ;
//      puis les MINERAIS : des filons dans la pierre (ils affleurent sur les parois des grottes) ;
//   4. la VÉGÉTATION : arbres (en forêts plus ou moins denses) et herbes hautes.
public static class TerrainGenerator
{
    // ------------------------------------------------------------------
    // Génération d'un chunk
    // ------------------------------------------------------------------

    public static void Generate(ChunkData d, TerrainSettings s)
    {
        const int SX = Chunk.SizeX, SZ = Chunk.SizeZ;
        int ox = d.coord.x * SX;
        int oz = d.coord.y * SZ;

        // 1) Hauteurs, avec une case de marge tout autour pour mesurer les pentes au bord du chunk
        const int W = SX + 2;
        var heights = new int[W * W];
        for (int i = -1; i <= SX; i++)
        for (int k = -1; k <= SZ; k++)
            heights[(i + 1) * W + (k + 1)] = SurfaceHeight(ox + i, oz + k, s);

        // 2) et 3) Couches et grottes, colonne par colonne
        var grassTop = new bool[SX * SZ];   // la surface de la colonne est de l'herbe intacte (pour la végétation)

        for (int x = 0; x < SX; x++)
        for (int z = 0; z < SZ; z++)
        {
            int wx = ox + x, wz = oz + z;
            int h = HAt(heights, x, z);
            bool rocky = IsRocky(h, HAt(heights, x + 1, z), HAt(heights, x - 1, z), HAt(heights, x, z + 1), HAt(heights, x, z - 1), wx, wz, s);
            bool sandy = !rocky && IsSandy(h, wx, wz, s);

            int col = ChunkData.Index(x, 0, z);
            for (int y = 0; y < h; y++)
            {
                if (IsBedrock(wx, y, wz, s.seed)) { d.blocks[col + y] = (byte)BlockType.Bedrock; continue; }
                if (s.caves && IsCave(wx, y, wz, h, s)) continue; // creusé : reste de l'air

                BlockType type;
                if (y == h - 1)                 type = rocky ? BlockType.Stone : sandy ? BlockType.Sand : BlockType.Grass;
                else if (y >= h - 4 && !rocky)  type = sandy ? BlockType.Sand : BlockType.Dirt;
                else                            type = BlockType.Stone;

                d.blocks[col + y] = (byte)type;
            }

            // Les creux sous le niveau de la mer se remplissent d'eau
            for (int y = h; y < s.seaLevel && y < Chunk.SizeY; y++)
                d.blocks[col + y] = (byte)BlockType.Water;

            int columnTop = Math.Max(h, Math.Min(s.seaLevel, Chunk.SizeY)) - 1;
            if (columnTop > d.highest) d.highest = columnTop;
            grassTop[x * SZ + z] = d.blocks[col + h - 1] == (byte)BlockType.Grass;
        }

        // Minerais : après les grottes (ils affleurent sur leurs parois), avant la végétation
        if (s.ores) PlaceOres(d, s, ox, oz);

        // 4) Végétation
        if (s.treeDensity > 0f) PlaceTrees(d, s, ox, oz);
        PlaceTallGrass(d, s, ox, oz, heights, grassTop);

        if (s.showcase && d.coord == Vector2Int.zero)
        {
            Force(d, 3, HAt(heights, 3, 3), 3, BlockType.StoneSlab);
            Force(d, 5, HAt(heights, 5, 3), 3, BlockType.Anvil);
            Force(d, 7, HAt(heights, 7, 3), 3, BlockType.AnvilRotated);
            Force(d, 9, HAt(heights, 9, 3), 3, BlockType.Torch);
        }
    }

    // Hauteur de la colonne (x, z) du chunk, x et z pouvant valoir -1 ou 16 (marge pour les pentes)
    static int HAt(int[] heights, int x, int z) => heights[(x + 1) * (Chunk.SizeX + 2) + (z + 1)];

    // Remplit un chunk à partir de données sauvegardées (même rangement que ChunkData.blocks)
    public static void ImportSaved(ChunkData d, byte[] saved)
    {
        Buffer.BlockCopy(saved, 0, d.blocks, 0, Chunk.DataLength);
        if (saved.Length >= Chunk.SaveLength)
            Buffer.BlockCopy(saved, Chunk.DataLength, d.state, 0, Chunk.DataLength);

        d.highest = -1;
        for (int x = 0; x < Chunk.SizeX; x++)
        for (int z = 0; z < Chunk.SizeZ; z++)
        {
            int col = ChunkData.Index(x, 0, z);
            for (int y = Chunk.SizeY - 1; y > d.highest; y--)
            {
                if (d.blocks[col + y] != 0)
                {
                    d.highest = y;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 1) Relief
    // ------------------------------------------------------------------

    // Hauteur de la surface (nombre de blocs pleins dans la colonne, avant les grottes) à une position MONDE
    public static int SurfaceHeight(int wx, int wz, TerrainSettings s)
    {
        float k = 1f / Math.Max(0.1f, s.scale);
        float x = wx * k, z = wz * k;

        // Continents : de très larges bosses et creux, sur des kilomètres
        float continent = Fbm2(x / 700f, z / 700f, 3, s.seed + 1);

        // Collines : relief moyen, partout
        float hills = Fbm2(x / 110f, z / 110f, 4, s.seed + 2);

        // Où sont les montagnes : 0 = plaine, 1 = massif montagneux (passage progressif)
        float mountainMask = SmoothStep(-0.05f, 0.30f, Fbm2(x / 420f, z / 420f, 2, s.seed + 3));

        // Forme des montagnes : bruit « en crêtes » (1 - |bruit|) qui dessine des arêtes et des sommets
        float ridge = 1f - Math.Abs(Fbm2(x / 160f, z / 160f, 4, s.seed + 4));
        ridge *= ridge;

        float h = s.baseHeight
                + continent * 18f
                + hills * s.hillHeight * (1f - 0.5f * mountainMask)
                + mountainMask * (0.25f + 0.75f * ridge) * s.mountainHeight;

        int ih = (int)Math.Round(h);

        // Au moins quelques blocs, et de la place au-dessus pour les arbres
        int max = Chunk.SizeY - 20;
        if (ih > max) ih = max;
        if (ih < 4) ih = 4;
        return ih;
    }

    // ------------------------------------------------------------------
    // 2) Couches
    // ------------------------------------------------------------------

    // La roche affleure-t-elle ? Oui sur les pentes raides (falaises) et en haute montagne
    static bool IsRocky(int h, int hxp, int hxm, int hzp, int hzm, int wx, int wz, TerrainSettings s)
    {
        int slope = Math.Max(Math.Abs(hxp - hxm), Math.Abs(hzp - hzm));
        if (slope >= 5) return true;

        // Limite de la roche, irrégulière (± 6 blocs) pour ne pas tracer une ligne droite sur les montagnes
        float rockLine = s.baseHeight + 18f + s.mountainHeight * 0.45f + Perlin2(wx / 24f, wz / 24f, s.seed + 5) * 6f;
        return h > rockLine;
    }

    // Points autour d'une colonne où l'on cherche de l'eau (à 2, 4 et 6 blocs), pour savoir si elle est au bord de l'eau
    static readonly int[] ShoreDX = { 2, -2, 0, 0, 4, -4, 0, 0, 3, 3, -3, -3, 6, -6, 0, 0, 4, 4, -4, -4 };
    static readonly int[] ShoreDZ = { 0, 0, 2, -2, 0, 0, 4, -4, 3, -3, 3, -3, 0, 0, 6, -6, 4, -4, 4, -4 };

    // Sable : au fond de l'eau, et sur les plages (jusqu'à 2 à 4 blocs au-dessus de l'eau, limite irrégulière).
    // Une cuvette sèche au niveau de la mer mais loin de toute eau reste de l'herbe.
    static bool IsSandy(int h, int wx, int wz, TerrainSettings s)
    {
        if (s.seaLevel <= 0) return false;
        if (h < s.seaLevel) return true; // sous l'eau

        int beachTop = s.seaLevel + 2 + (int)Math.Round(Perlin2(wx / 40f, wz / 40f, s.seed + 6) * 2f);
        if (h > beachTop) return false;

        for (int i = 0; i < ShoreDX.Length; i++)
            if (SurfaceHeight(wx + ShoreDX[i], wz + ShoreDZ[i], s) < s.seaLevel) return true;

        return false;
    }

    // Bedrock : toujours tout en bas, puis de plus en plus rare sur les 3 couches au-dessus (comme Minecraft)
    static bool IsBedrock(int wx, int y, int wz, int seed)
    {
        if (y == 0) return true;
        if (y > 3) return false;
        return Hash01(wx + y * 1013, wz - y * 7919, seed + 41) < (4 - y) / 5f;
    }

    // ------------------------------------------------------------------
    // 3) Grottes
    // ------------------------------------------------------------------

    // La case (wx, y, wz) est-elle creusée ? h = hauteur de la surface de sa colonne.
    public static bool IsCave(int wx, int y, int wz, int h, TerrainSettings s)
    {
        if (y <= 0) return false;                // le fond du monde reste plein
        if (s.caveDensity <= 0f) return false;

        int depth = h - y;                       // 1 = le bloc de surface

        // Tunnels (« spaghettis ») : là où DEUX bruits 3D sont tous les deux proches de zéro, on obtient
        // des tubes qui serpentent.
        float radius = 0.05f + 0.06f * s.caveDensity;

        // Près de la surface, les tunnels se referment (sinon le sol serait couvert de tranchées),
        // sauf dans quelques zones d'entrée : là, ils débouchent à l'air libre.
        if (depth < 8)
        {
            // Pas d'entrée près de l'eau : une grotte ouverte sous un lac le laisserait suspendu au-dessus du vide
            bool entrance = h > s.seaLevel + 6 && Perlin2(wx / 96f, wz / 96f, s.seed + 15) > 0.45f;
            if (!entrance) radius *= Math.Max(0f, (depth - 2) / 6f);
        }

        if (radius > 0f)
        {
            float a = Perlin3(wx / 42f, y / 30f, wz / 42f, s.seed + 11);
            float b = Perlin3(wx / 42f, y / 30f, wz / 42f, s.seed + 12);
            if (a * a + b * b < radius * radius) return true;
        }

        // Cavernes (« gruyère ») : grands volumes vides, seulement en profondeur pour ne pas trouer le sol
        if (depth > 10)
        {
            float c = Perlin3(wx / 70f, y / 38f, wz / 70f, s.seed + 13) + 0.35f * Perlin3(wx / 22f, y / 16f, wz / 22f, s.seed + 14);
            float fade = Math.Min(1f, (depth - 10) / 12f);   // les cavernes s'ouvrent progressivement en descendant
            float threshold = 0.62f - 0.22f * s.caveDensity;
            if (c * fade > threshold) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Minerais
    // ------------------------------------------------------------------

    readonly struct OreSpec
    {
        public readonly BlockType type;
        public readonly int veinsPerChunk, veinSize, minY, maxY;

        public OreSpec(BlockType type, int veinsPerChunk, int veinSize, int minY, int maxY)
        {
            this.type = type; this.veinsPerChunk = veinsPerChunk; this.veinSize = veinSize;
            this.minY = minY; this.maxY = maxY;
        }
    }

    // Inspiré de Minecraft : le charbon partout, le fer plus bas, l'or et le diamant tout au fond
    static readonly OreSpec[] Ores =
    {
        new OreSpec(BlockType.CoalOre,    20, 12, 5, 130),
        new OreSpec(BlockType.IronOre,    10,  8, 5,  70),
        new OreSpec(BlockType.GoldOre,     3,  7, 5,  32),
        new OreSpec(BlockType.DiamondOre,  1,  6, 5,  16),
        new OreSpec(BlockType.RedstoneOre, 8,  7, 5,  16),
    };

    static readonly int[] StepX = { 1, -1, 0, 0, 0, 0 };
    static readonly int[] StepY = { 0, 0, 1, -1, 0, 0 };
    static readonly int[] StepZ = { 0, 0, 0, 0, 1, -1 };

    // Chaque filon naît dans un chunk (position tirée au hasard, toujours la même pour une graine donnée) et
    // grandit par une marche au hasard. Un filon peut déborder sur le chunk voisin : chaque chunk parcourt
    // aussi les filons de ses 8 voisins et pose la partie qui tombe chez lui. Le minerai ne remplace que la pierre.
    static void PlaceOres(ChunkData d, TerrainSettings s, int ox, int oz)
    {
        for (int ci = -1; ci <= 1; ci++)
        for (int cj = -1; cj <= 1; cj++)
        {
            int ncx = d.coord.x + ci, ncz = d.coord.y + cj;

            for (int o = 0; o < Ores.Length; o++)
            {
                OreSpec ore = Ores[o];
                for (int v = 0; v < ore.veinsPerChunk; v++)
                {
                    uint rng = VeinSeed(ncx, ncz, o, v, s.seed);
                    int x = ncx * Chunk.SizeX + (int)(Rand(ref rng) * Chunk.SizeX);
                    int z = ncz * Chunk.SizeZ + (int)(Rand(ref rng) * Chunk.SizeZ);
                    int y = ore.minY + (int)(Rand(ref rng) * (ore.maxY - ore.minY + 1));
                    int size = 1 + (int)(Rand(ref rng) * ore.veinSize);

                    // Le filon ne peut pas atteindre ce chunk : inutile de le parcourir
                    if (x < ox - size || x >= ox + Chunk.SizeX + size || z < oz - size || z >= oz + Chunk.SizeZ + size) continue;

                    for (int k = 0; k < size; k++)
                    {
                        SetOre(d, x - ox, y, z - oz, ore.type);
                        int step = (int)(Rand(ref rng) * 6f);
                        x += StepX[step]; y += StepY[step]; z += StepZ[step];
                    }
                }
            }
        }
    }

    static void SetOre(ChunkData d, int x, int y, int z, BlockType type)
    {
        if (!Inside(x, y, z)) return;
        int i = ChunkData.Index(x, y, z);
        if (d.blocks[i] == (byte)BlockType.Stone) d.blocks[i] = (byte)type;
    }

    static uint VeinSeed(int cx, int cz, int ore, int vein, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u;
            h ^= (uint)cx * 0x85EBCA6Bu; h = (h << 13) | (h >> 19);
            h ^= (uint)cz * 0xC2B2AE35u; h = (h << 13) | (h >> 19);
            h ^= (uint)(ore * 7919 + vein * 104729) * 0x27D4EB2Fu;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
            return h == 0 ? 1u : h;
        }
    }

    // Nombre pseudo-aléatoire entre 0 et 1 (xorshift)
    static float Rand(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0xFFFFFF) / (float)0x1000000;
    }

    // ------------------------------------------------------------------
    // 4) Végétation
    // ------------------------------------------------------------------

    const int TreeCell = 6;         // au plus un arbre par carré de 6 x 6 blocs
    const int TreeReach = 2;        // rayon des feuilles autour du tronc

    // Les arbres sont choisis sur une grille MONDE : chaque chunk regarde aussi les arbres de ses voisins
    // dont les feuilles débordent chez lui. Comme tout est déterministe, les deux moitiés se raccordent.
    static void PlaceTrees(ChunkData d, TerrainSettings s, int ox, int oz)
    {
        int gx0 = FloorDiv(ox - TreeReach, TreeCell), gx1 = FloorDiv(ox + Chunk.SizeX - 1 + TreeReach, TreeCell);
        int gz0 = FloorDiv(oz - TreeReach, TreeCell), gz1 = FloorDiv(oz + Chunk.SizeZ - 1 + TreeReach, TreeCell);

        for (int gx = gx0; gx <= gx1; gx++)
        for (int gz = gz0; gz <= gz1; gz++)
        {
            // Densité : forêts (bruit lent) et quelques arbres isolés dans les plaines
            float forest = Fbm2(gx * TreeCell / 160f, gz * TreeCell / 160f, 2, s.seed + 21) * 0.5f + 0.5f;
            float chance = s.treeDensity * (0.06f + 0.85f * SmoothStep(0.5f, 0.68f, forest));
            if (Hash01(gx, gz, s.seed + 22) >= chance) continue;

            // Position dans la cellule (jamais au bord : deux arbres voisins ne se touchent pas)
            int tx = gx * TreeCell + 1 + (int)(Hash01(gx, gz, s.seed + 23) * (TreeCell - 2));
            int tz = gz * TreeCell + 1 + (int)(Hash01(gx, gz, s.seed + 24) * (TreeCell - 2));

            // Sur de l'herbe uniquement : ni roche (pente, altitude), ni sable (plage, fond de l'eau),
            // ni sol creusé par une grotte
            int h = SurfaceHeight(tx, tz, s);
            if (IsSandy(h, tx, tz, s)) continue;
            if (IsRocky(h, SurfaceHeight(tx + 1, tz, s), SurfaceHeight(tx - 1, tz, s),
                           SurfaceHeight(tx, tz + 1, s), SurfaceHeight(tx, tz - 1, s), tx, tz, s)) continue;
            if (s.caves && (IsCave(tx, h - 1, tz, h, s) || IsCave(tx, h - 2, tz, h, s))) continue;

            int trunk = 4 + (int)(Hash01(gx, gz, s.seed + 25) * 3f); // 4 à 6 blocs
            PlaceTree(d, tx - ox, tz - oz, h, trunk, gx, gz, s.seed);
        }
    }

    // Arbre façon chêne : tronc, deux couches larges de feuilles puis deux couches étroites.
    // (x, z) en coordonnées LOCALES du chunk, éventuellement hors du chunk : seules les cases à l'intérieur sont posées.
    static void PlaceTree(ChunkData d, int x, int z, int groundY, int trunk, int gx, int gz, int seed)
    {
        int top = groundY + trunk; // première case au-dessus du tronc

        for (int y = top - 3; y <= top; y++)
        {
            int r = y <= top - 2 ? 2 : 1;
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                bool corner = Math.Abs(dx) == r && Math.Abs(dz) == r;
                if (corner && y == top) continue;   // sommet en forme de croix
                if (corner && Hash01(gx * 31 + dx * 7 + y, gz * 17 + dz * 5, seed + 26) < 0.5f) continue; // coins irréguliers
                TrySet(d, x + dx, y, z + dz, BlockType.Leaves);
            }
        }

        // Le tronc remplace les feuilles qui le chevauchent
        for (int y = groundY; y < top; y++)
            SetIfAirOrLeaves(d, x, y, z, BlockType.Log);
    }

    static void PlaceTallGrass(ChunkData d, TerrainSettings s, int ox, int oz, int[] heights, bool[] grassTop)
    {
        const int W = Chunk.SizeX + 2;

        for (int x = 0; x < Chunk.SizeX; x++)
        for (int z = 0; z < Chunk.SizeZ; z++)
        {
            if (!grassTop[x * Chunk.SizeZ + z]) continue;

            int wx = ox + x, wz = oz + z;
            float meadow = Fbm2(wx / 50f, wz / 50f, 2, s.seed + 31) * 0.5f + 0.5f;
            float chance = 0.04f + 0.30f * SmoothStep(0.45f, 0.75f, meadow);
            if (Hash01(wx, wz, s.seed + 32) < chance)
                TrySet(d, x, heights[(x + 1) * W + (z + 1)], z, BlockType.TallGrass);
        }
    }

    // ------------------------------------------------------------------
    // Pose de blocs
    // ------------------------------------------------------------------

    static bool Inside(int x, int y, int z)
    {
        return (uint)x < Chunk.SizeX && (uint)y < Chunk.SizeY && (uint)z < Chunk.SizeZ;
    }

    // Place un bloc seulement s'il est dans le chunk et que la case est vide
    static void TrySet(ChunkData d, int x, int y, int z, BlockType type)
    {
        if (!Inside(x, y, z)) return;
        if (d.blocks[ChunkData.Index(x, y, z)] != 0) return;
        d.SetBlock(x, y, z, type);
    }

    static void SetIfAirOrLeaves(ChunkData d, int x, int y, int z, BlockType type)
    {
        if (!Inside(x, y, z)) return;
        byte current = d.blocks[ChunkData.Index(x, y, z)];
        if (current != 0 && current != (byte)BlockType.Leaves) return;
        d.SetBlock(x, y, z, type);
    }

    // Place un bloc dans le chunk, même si la case est occupée
    static void Force(ChunkData d, int x, int y, int z, BlockType type)
    {
        if (!Inside(x, y, z)) return;
        d.SetBlock(x, y, z, type);
    }

    // ------------------------------------------------------------------
    // Bruits (faits maison, thread-safe)
    // ------------------------------------------------------------------

    // Somme de plusieurs octaves de bruit de Perlin 2D (environ entre -0,7 et 0,7)
    static float Fbm2(float x, float z, int octaves, int seed)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Perlin2(x, z, seed + i * 1013) * amp;
            norm += amp;
            amp *= 0.5f;
            x *= 2.03f;
            z *= 2.03f;
        }
        return sum / norm;
    }

    static float Perlin2(float x, float z, int seed)
    {
        int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
        float fx = x - x0, fz = z - z0;
        float u = Fade(fx), v = Fade(fz);

        float n00 = Grad2(Hash(x0, 0, z0, seed), fx, fz);
        float n10 = Grad2(Hash(x0 + 1, 0, z0, seed), fx - 1f, fz);
        float n01 = Grad2(Hash(x0, 0, z0 + 1, seed), fx, fz - 1f);
        float n11 = Grad2(Hash(x0 + 1, 0, z0 + 1, seed), fx - 1f, fz - 1f);

        float a = n00 + (n10 - n00) * u;
        float b = n01 + (n11 - n01) * u;
        return a + (b - a) * v; // environ entre -1 et 1
    }

    static float Perlin3(float x, float y, float z, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
        float fx = x - x0, fy = y - y0, fz = z - z0;
        float u = Fade(fx), v = Fade(fy), w = Fade(fz);

        float n000 = Grad3(Hash(x0, y0, z0, seed), fx, fy, fz);
        float n100 = Grad3(Hash(x0 + 1, y0, z0, seed), fx - 1f, fy, fz);
        float n010 = Grad3(Hash(x0, y0 + 1, z0, seed), fx, fy - 1f, fz);
        float n110 = Grad3(Hash(x0 + 1, y0 + 1, z0, seed), fx - 1f, fy - 1f, fz);
        float n001 = Grad3(Hash(x0, y0, z0 + 1, seed), fx, fy, fz - 1f);
        float n101 = Grad3(Hash(x0 + 1, y0, z0 + 1, seed), fx - 1f, fy, fz - 1f);
        float n011 = Grad3(Hash(x0, y0 + 1, z0 + 1, seed), fx, fy - 1f, fz - 1f);
        float n111 = Grad3(Hash(x0 + 1, y0 + 1, z0 + 1, seed), fx - 1f, fy - 1f, fz - 1f);

        float x00 = n000 + (n100 - n000) * u;
        float x10 = n010 + (n110 - n010) * u;
        float x01 = n001 + (n101 - n001) * u;
        float x11 = n011 + (n111 - n011) * u;
        float y0v = x00 + (x10 - x00) * v;
        float y1v = x01 + (x11 - x01) * v;
        return y0v + (y1v - y0v) * w;
    }

    static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    // 8 directions en 2D
    static float Grad2(uint h, float x, float z)
    {
        switch (h & 7)
        {
            case 0: return x + z;
            case 1: return -x + z;
            case 2: return x - z;
            case 3: return -x - z;
            case 4: return x;
            case 5: return -x;
            case 6: return z;
            default: return -z;
        }
    }

    // Les 12 directions classiques du bruit de Perlin 3D (milieux des arêtes d'un cube)
    static float Grad3(uint h, float x, float y, float z)
    {
        switch (h % 12)
        {
            case 0:  return x + y;
            case 1:  return -x + y;
            case 2:  return x - y;
            case 3:  return -x - y;
            case 4:  return x + z;
            case 5:  return -x + z;
            case 6:  return x - z;
            case 7:  return -x - z;
            case 8:  return y + z;
            case 9:  return -y + z;
            case 10: return y - z;
            default: return -y - z;
        }
    }

    static uint Hash(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 0x27D4EB2Du;
            h ^= (uint)x * 0x85EBCA6Bu; h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0xC2B2AE35u; h = (h << 13) | (h >> 19);
            h ^= (uint)z * 0x165667B1u;
            h ^= h >> 15; h *= 0x2C1B3C6Du;
            h ^= h >> 12; h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }
    }

    // Nombre pseudo-aléatoire stable entre 0 et 1 pour une position (x, z)
    public static float Hash01(int x, int z, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + salt * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / (float)0x1000000;
        }
    }

    static float SmoothStep(float a, float b, float t)
    {
        t = (t - a) / (b - a);
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        return t * t * (3f - 2f * t);
    }

    static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
