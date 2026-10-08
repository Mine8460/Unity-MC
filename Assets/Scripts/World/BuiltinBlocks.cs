using System.Collections.Generic;
using UnityEngine;

// Blocs intégrés au jeu : la redstone. Ils sont créés en mémoire, avec des textures dessinées par le code, tant qu'aucun
// fichier de bloc (Assets/Resources/Blocks) n'a le même numéro. Pour mettre tes propres textures, crée le fichier d'un de
// ces blocs avec le MÊME « Id » : il remplace le bloc intégré.
public static class BuiltinBlocks
{
    public static BlockDefinition[] Create(HashSet<int> takenIds)
    {
        var b = new Builder(takenIds);
        var tex = new Textures();

        // --- Minerai et bloc ---
        BlockDefinition ore = b.Add(Redstone.Ore, "Minerai de redstone");
        if (ore != null)
        {
            ore.side.albedo = tex.Ore;
            ore.breakTime = 3f;
            ore.tool = ToolKind.Pickaxe;
            ore.harvestLevel = 3; // pioche en fer
            ore.dropItem = ItemType.RedstoneDust;
            ore.dropCount = 4;
            ore.dropCountRandom = 1; // 4 ou 5 poussières
        }

        BlockDefinition block = b.Add(Redstone.Block, "Bloc de redstone");
        if (block != null)
        {
            block.side.albedo = tex.RedstoneBlock;
            block.breakTime = 5f;
            block.tool = ToolKind.Pickaxe;
            block.harvestLevel = 1;
        }

        // --- Poussière : fil plat, posé sur un bloc plein ---
        BlockDefinition dust = b.Add(Redstone.Dust, "Poussière de redstone");
        if (dust != null)
        {
            dust.shape = BlockShape.Wire;
            dust.side.albedo = tex.Dust;
            dust.opaque = false;
            dust.collidable = false;
            dust.support = SupportRule.OpaqueBelow;
            dust.breakTime = 0f;
            dust.dropItem = ItemType.RedstoneDust;
        }

        // --- Lampe ---
        BlockDefinition lamp = b.Add(Redstone.Lamp, "Lampe de redstone");
        if (lamp != null)
        {
            lamp.side.albedo = tex.LampOff;
            lamp.breakTime = 0.45f;
        }
        BlockDefinition lampLit = b.Add(Redstone.LampLit, "Lampe de redstone allumée");
        if (lampLit != null)
        {
            lampLit.side.albedo = tex.LampOn;
            lampLit.emission = 15;
            lampLit.breakTime = 0.45f;
            lampLit.dropBlock = lamp;
        }

        // --- Torches, leviers, boutons : au sol et contre les 4 murs ---
        BlockDefinition floorTorch = null;
        BlockDefinition floorLever = null;
        BlockDefinition floorButton = null;

        for (int orient = 0; orient < 5; orient++)
        {
            Vector3Int attach = Redstone.AttachDirs[orient];
            bool wall = orient > 0;

            // Torches : allumée (lumière 7) ou éteinte
            for (int lit = 1; lit >= 0; lit--)
            {
                BlockDefinition tc = b.Add((lit == 1 ? Redstone.TorchLit : Redstone.TorchUnlit) + orient,
                                        lit == 1 ? "Torche de redstone" : "Torche de redstone éteinte");
                if (tc == null) continue;
                tc.shape = BlockShape.Model;
                tc.opaque = false;
                tc.collidable = false;
                tc.side.albedo = tex.Torch(lit == 1, wall);
                tc.lightMode = lit == 1 ? LightMode.FullBright : LightMode.Pixel;
                tc.emission = lit == 1 ? 7 : 0;
                tc.support = wall ? SupportRule.SolidAttached : SupportRule.SolidBelow;
                tc.attachDir = attach;
                tc.breakTime = 0f;
                tc.boxes = new[] { WallBox(wall ? new Vector3(7, 3, 14) : new Vector3(7, 0, 7), wall ? new Vector3(9, 13, 16) : new Vector3(9, 10, 9), wall ? orient : 0, false) };
                if (orient == 0 && lit == 1) floorTorch = tc;
                else tc.dropBlock = floorTorch; // toutes les variantes lâchent la torche allumée posée au sol
            }

            // Leviers : poignée penchée à gauche (éteint) ou à droite (allumé) ; contre un mur : en bas / en haut
            for (int on = 0; on <= 1; on++)
            {
                BlockDefinition l = b.Add((on == 1 ? Redstone.LeverOn : Redstone.LeverOff) + orient, "Levier");
                if (l == null) continue;
                l.shape = BlockShape.Model;
                l.opaque = false;
                l.collidable = false;
                l.side.albedo = tex.LeverBase;
                l.support = wall ? SupportRule.SolidAttached : SupportRule.SolidBelow;
                l.attachDir = attach;
                l.breakTime = 0.5f;
                l.boxes = LeverBoxes(on == 1, orient, tex.LeverHandle);
                if (orient == 0 && on == 0) floorLever = l;
                else l.dropBlock = floorLever;
            }

            // Boutons de pierre : une petite plaque, plus plate une fois enfoncée
            for (int pressed = 0; pressed <= 1; pressed++)
            {
                BlockDefinition btn = b.Add((pressed == 1 ? Redstone.ButtonPressed : Redstone.ButtonOff) + orient, "Bouton de pierre");
                if (btn == null) continue;
                btn.shape = BlockShape.Model;
                btn.opaque = false;
                btn.collidable = false;
                btn.side.albedo = tex.Button;
                btn.support = wall ? SupportRule.SolidAttached : SupportRule.SolidBelow;
                btn.attachDir = attach;
                btn.breakTime = 0.5f;
                float depth = pressed == 1 ? 1f : 2f;
                btn.boxes = new[] { WallBox(new Vector3(5, 0, 6), new Vector3(11, depth, 10), orient, true) };
                if (orient == 0 && pressed == 0) floorButton = btn;
                else btn.dropBlock = floorButton;
            }
        }

        // --- Répéteur ---
        BlockDefinition rep = b.Add(Redstone.Repeater, "Répéteur de redstone");
        if (rep != null)
        {
            rep.shape = BlockShape.Repeater;
            rep.opaque = false;
            rep.collidable = true;
            rep.top.albedo = tex.RepeaterTop;
            rep.side.albedo = tex.RepeaterSide;
            rep.bottom.albedo = tex.RepeaterTorch;   // texture des deux torches
            rep.support = SupportRule.OpaqueBelow;
            rep.breakTime = 0f;
            rep.dropItem = ItemType.Repeater;
        }

        // --- Four : un cube dont l'AVANT (+Z) porte l'ouverture ; tourné vers le joueur à la pose ---
        BlockDefinition furnace = b.Add(FurnaceIds.Block, "Four");
        if (furnace != null)
        {
            SetupFurnace(furnace, tex.FurnaceSide, tex.FurnaceTop, tex.FurnaceFrontOff);
            furnace.emission = 0;
        }
        BlockDefinition furnaceLit = b.Add(FurnaceIds.Lit, "Four allumé");
        if (furnaceLit != null)
        {
            SetupFurnace(furnaceLit, tex.FurnaceSide, tex.FurnaceTop, tex.FurnaceFrontOn);
            furnaceLit.emission = 13;
            furnaceLit.dropBlock = furnace;
        }

        return b.list.ToArray();
    }

    static void SetupFurnace(BlockDefinition d, Texture2D side, Texture2D top, Texture2D front)
    {
        d.shape = BlockShape.Model;
        d.orientation = Orientation.Horizontal;
        d.side.albedo = side;
        d.top.albedo = top;
        d.bottom.albedo = top;
        d.boxes = new[] { new ModelBox { from = Vector3.zero, to = new Vector3(16, 16, 16), top = top, bottom = top, side = side, front = front } };
        d.opaque = true;
        d.collidable = true;
        d.breakTime = 3.5f;
        d.tool = ToolKind.Pickaxe;
        d.harvestLevel = 1;
    }

    sealed class Builder
    {
        public readonly List<BlockDefinition> list = new List<BlockDefinition>();
        readonly HashSet<int> taken;

        public Builder(HashSet<int> taken) { this.taken = taken; }

        // Un bloc, sauf si un fichier a déjà ce numéro (il l'emporte : null)
        public BlockDefinition Add(int id, string name)
        {
            if (taken.Contains(id)) return null;
            var d = ScriptableObject.CreateInstance<BlockDefinition>();
            d.id = (BlockType)id;
            d.name = name;
            d.displayName = name;
            d.hideFlags = HideFlags.DontSave;
            list.Add(d);
            return d;
        }
    }

    // ------------------------------------------------------------------
    // Géométrie des composants fixés
    // ------------------------------------------------------------------

    // Une boîte définie « au sol » (en 16èmes de bloc), ramenée contre le mur `orient` (0 = au sol : inchangée).
    //   tiltUp : la boîte est une plaque posée sur le sol (levier, bouton) : contre un mur, sa hauteur devient la
    //            distance au mur, et sa largeur x devient la hauteur sur le mur.
    //   sinon (torche) : la boîte est déjà donnée contre le mur nord ; on la tourne pour les autres murs.
    static ModelBox WallBox(Vector3 from, Vector3 to, int orient, bool tiltUp)
    {
        if (orient == 0) return new ModelBox { from = from, to = to };

        Vector3 a, b;
        if (tiltUp)
        {
            // sol -> mur nord : (x, y, z) -> (z, x, 16 - y)
            a = new Vector3(from.z, from.x, 16f - from.y);
            b = new Vector3(to.z, to.x, 16f - to.y);
        }
        else
        {
            a = from;
            b = to;
        }

        // On tourne autour de l'axe vertical : nord (1) -> sud (2) -> est (3) -> ouest (4)
        for (int turns = RotationsFromNorth(orient); turns > 0; turns--)
        {
            // (x, z) -> (z, 16 - x)
            a = new Vector3(a.z, a.y, 16f - a.x);
            b = new Vector3(b.z, b.y, 16f - b.x);
        }

        return new ModelBox { from = Vector3.Min(a, b), to = Vector3.Max(a, b) };
    }

    // Nombre de quarts de tour à appliquer au modèle « mur nord » pour atteindre le mur demandé
    static int RotationsFromNorth(int orient)
    {
        switch (orient)
        {
            case 1: return 0; // nord (+Z)
            case 3: return 1; // est (+X)
            case 2: return 2; // sud (-Z)
            default: return 3; // ouest (-X)
        }
    }

    // Levier : une plaque et une poignée en escalier, penchée d'un côté (éteint) ou de l'autre (allumé)
    static ModelBox[] LeverBoxes(bool on, int orient, Texture2D handle)
    {
        float lean = on ? 1f : -1f;
        return new[]
        {
            WallBox(new Vector3(4, 0, 5), new Vector3(12, 2, 11), orient, true),
            LeverStep(lean, 0f, 2, 5, orient, handle),
            LeverStep(lean, 1f, 5, 8, orient, handle),
            LeverStep(lean, 2f, 8, 11, orient, handle),
        };
    }

    static ModelBox LeverStep(float lean, float step, float y0, float y1, int orient, Texture2D handle)
    {
        float x0 = 7f + lean * step, x1 = 9f + lean * step;
        ModelBox box = WallBox(new Vector3(Mathf.Min(x0, x1), y0, 7), new Vector3(Mathf.Max(x0, x1), y1, 9), orient, true);
        box.side = handle;
        box.top = handle;
        box.bottom = handle;
        return box;
    }

    // ------------------------------------------------------------------
    // Textures dessinées par le code (16 x 16)
    // ------------------------------------------------------------------

    sealed class Textures
    {
        const int N = 16;

        public readonly Texture2D Ore, RedstoneBlock, Dust, LampOff, LampOn, LeverBase, LeverHandle, Button,
                                  RepeaterTop, RepeaterSide, RepeaterTorch;

        public readonly Texture2D FurnaceSide, FurnaceTop, FurnaceFrontOff, FurnaceFrontOn;

        readonly Texture2D torchFloorLit, torchFloorUnlit, torchWallLit, torchWallUnlit;

        public Textures()
        {
            Ore = Make("redstone_ore", (x, y) =>
            {
                if (IsOreSpeck(x, y)) return Shade(new Color32(200, 20, 20, 255), Noise(x, y, 5), 0.25f);
                return Stone(x, y);
            });
            RedstoneBlock = Make("redstone_block", (x, y) => Shade(new Color32(190, 24, 20, 255), Noise(x, y, 7), 0.45f));
            Dust = Make("redstone_dust", (x, y) => Shade(new Color32(205, 205, 205, 255), Noise(x, y, 9), 0.2f));
            LampOff = Make("redstone_lamp", (x, y) => Lamp(x, y, false));
            LampOn = Make("redstone_lamp_on", (x, y) => Lamp(x, y, true));
            LeverBase = Make("lever_base", Stone);
            LeverHandle = Make("lever_handle", (x, y) => Shade(new Color32(132, 100, 56, 255), Noise(x, y, 11), 0.25f));
            Button = Make("stone_button", Stone);

            RepeaterTop = Make("repeater_top", RepeaterTopPixel);
            RepeaterSide = Make("repeater_side", Stone);
            // Les torches du répéteur : une texture uniforme (elles sont teintées par la puissance, comme le fil)
            RepeaterTorch = Make("repeater_torch", (x, y) => Shade(new Color32(205, 205, 205, 255), Noise(x, y, 13), 0.2f));

            FurnaceSide = Make("furnace_side", (x, y) => Shade(new Color32(112, 112, 112, 255), Noise(x, y, 21), 0.2f));
            FurnaceTop = Make("furnace_top", (x, y) => Shade(new Color32(98, 98, 98, 255), Noise(x, y, 23), 0.15f));
            FurnaceFrontOff = Make("furnace_front", (x, y) => FurnaceFront(x, y, false));
            FurnaceFrontOn = Make("furnace_front_on", (x, y) => FurnaceFront(x, y, true));

            torchFloorLit = Make("redstone_torch_lit", (x, y) => TorchPixel(y, true, 8, 9));
            torchFloorUnlit = Make("redstone_torch_unlit", (x, y) => TorchPixel(y, false, 8, 9));
            torchWallLit = Make("redstone_torch_wall_lit", (x, y) => TorchPixel(y, true, 11, 12));
            torchWallUnlit = Make("redstone_torch_wall_unlit", (x, y) => TorchPixel(y, false, 11, 12));
        }

        public Texture2D Torch(bool lit, bool wall) => wall ? (lit ? torchWallLit : torchWallUnlit) : (lit ? torchFloorLit : torchFloorUnlit);

        // ----- dessin -----

        delegate Color32 Painter(int x, int y);

        static Texture2D Make(string name, Painter paint)
        {
            var pixels = new Color32[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                pixels[y * N + x] = paint(x, y);

            var t = new Texture2D(N, N, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            t.SetPixels32(pixels);
            t.Apply();
            return t;
        }

        // Bruit déterministe entre 0 et 1
        static float Noise(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535f;
            }
        }

        // Une couleur de base, éclaircie ou assombrie selon le bruit
        static Color32 Shade(Color32 c, float noise, float amount)
        {
            float f = 1f + (noise - 0.5f) * 2f * amount;
            return new Color32(Clamp(c.r * f), Clamp(c.g * f), Clamp(c.b * f), 255);
        }

        static byte Clamp(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);

        static Color32 Stone(int x, int y) => Shade(new Color32(125, 125, 125, 255), Noise(x, y, 3), 0.18f);

        // Taches rouges du minerai : quelques amas de pixels
        static bool IsOreSpeck(int x, int y)
        {
            int[,] specks = { { 3, 4 }, { 4, 4 }, { 3, 5 }, { 10, 3 }, { 11, 3 }, { 11, 4 }, { 6, 9 }, { 7, 9 }, { 7, 10 }, { 12, 11 }, { 12, 12 }, { 3, 12 }, { 4, 12 }, { 4, 13 } };
            for (int i = 0; i < specks.GetLength(0); i++)
                if (specks[i, 0] == x && specks[i, 1] == y) return true;
            return false;
        }

        // Face avant du four : de la pierre, avec une ouverture en bas (sombre, ou pleine de braises) et une grille en haut
        static Color32 FurnaceFront(int x, int y, bool lit)
        {
            // y = 0 en bas de l'image
            bool opening = x >= 3 && x <= 12 && y >= 2 && y <= 8;
            if (opening)
            {
                if (!lit) return Shade(new Color32(28, 28, 30, 255), Noise(x, y, 25), 0.3f);
                float glow = 1f - (y - 2) / 8f;                         // plus vif en bas
                var c = Color32.Lerp(new Color32(255, 210, 90, 255), new Color32(210, 70, 20, 255), 1f - glow);
                return Shade(c, Noise(x, y, 27), 0.18f);
            }

            bool vent = x >= 5 && x <= 10 && y >= 11 && y <= 13 && (x + y) % 2 == 0;
            if (vent) return new Color32(38, 38, 40, 255);

            bool frame = x >= 2 && x <= 13 && y >= 1 && y <= 9;       // cadre autour de l'ouverture
            return Shade(frame ? new Color32(86, 86, 86, 255) : new Color32(112, 112, 112, 255), Noise(x, y, 29), 0.2f);
        }

        static Color32 Lamp(int x, int y, bool lit)
        {
            bool line = x % 8 == 0 || y % 8 == 0 || x == 15 || y == 15;
            if (lit) return line ? new Color32(255, 225, 140, 255) : Shade(new Color32(250, 190, 90, 255), Noise(x, y, 17), 0.15f);
            return line ? new Color32(120, 78, 52, 255) : Shade(new Color32(82, 52, 36, 255), Noise(x, y, 17), 0.15f);
        }

        // Torche : un bâton brun sur toute la largeur, avec une pointe rouge (allumée) ou sombre (éteinte).
        // Les rangées qui se trouvent dans la boîte de la torche portent la couleur ; le reste est transparent.
        static Color32 TorchPixel(int y, bool lit, int tipFrom, int tipTo)
        {
            if (y >= tipFrom && y <= tipTo + 1) return lit ? new Color32(235, 40, 30, 255) : new Color32(90, 20, 18, 255);
            return new Color32(122, 88, 48, 255);
        }

        // Dessus du répéteur : fond de pierre sombre et flèche vers le haut de la texture (le sens de sortie)
        static Color32 RepeaterTopPixel(int x, int y)
        {
            bool shaft = (x == 7 || x == 8) && y >= 2 && y <= 11;
            bool head = y >= 9 && y <= 13 && Mathf.Abs(x - 7.5f) <= (13 - y) * 0.9f + 0.5f;  // pointe en haut
            if (shaft || head) return new Color32(205, 205, 205, 255);
            return Shade(new Color32(112, 112, 112, 255), Noise(x, y, 19), 0.14f);
        }
    }
}
