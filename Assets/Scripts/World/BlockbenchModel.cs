using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Lit un modèle exporté par Blockbench au format "Java Block/Item" (le JSON de modèles de Minecraft)
// et le convertit en quads prêts à être ajoutés au mesh d'un chunk.
//
// Conversion de repère : Blockbench est en repère droit, Unity en repère gauche.
// On inverse donc l'axe Z (z_unity = 1 - z_bb). Conséquence : la face "north" de Blockbench
// devient la face +Z dans Unity, et "south" devient -Z. "east" reste +X.
// L'apparence du modèle est conservée (textures dans le bon sens, vu de l'extérieur).
public static class BlockbenchModel
{
    // Ordre = celui des faces dans Chunk : haut, bas, +Z, -Z, +X, -X
    static readonly string[] FaceNames = { "up", "down", "north", "south", "east", "west" };

    static readonly Vector3[] UnityDirs =
        { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left };

    // Direction de chaque face dans le repère Blockbench
    static readonly Vector3[] BbDirs =
        { new(0, 1, 0), new(0, -1, 0), new(0, 0, -1), new(0, 0, 1), new(1, 0, 0), new(-1, 0, 0) };

    // Convertit un modèle. Lève une exception (FormatException...) si le fichier est invalide.
    //   tileByName  : retrouve une tuile d'atlas d'après le nom de la texture (renvoie -1 si inconnue)
    //   defaultTile : tuile utilisée quand la texture n'est pas reconnue par son nom
    public static void Bake(string json, Func<string, int> tileByName, int defaultTile,
                            out ModelQuad[] quads, out Box[] boxes)
    {
        var root = MiniJson.Parse(json) as Dictionary<string, object>;
        if (root == null) throw new FormatException("le fichier n'est pas un objet JSON");

        // Espace des coordonnées UV : 16 par défaut, sinon "texture_size"
        float texW = 16f, texH = 16f;
        if (root.TryGetValue("texture_size", out object tsObj) && tsObj is List<object> ts && ts.Count >= 2)
        {
            texW = Num(ts[0]);
            texH = Num(ts[1]);
        }

        // Table "clé de texture" -> chemin
        var textures = new Dictionary<string, string>();
        if (root.TryGetValue("textures", out object texObj) && texObj is Dictionary<string, object> texDict)
        {
            foreach (var kv in texDict)
                if (kv.Value is string path) textures[kv.Key] = path;
        }

        if (!root.TryGetValue("elements", out object elObj) || !(elObj is List<object> elements))
            throw new FormatException("aucun \"elements\" (exporte le modèle en Java Block/Item)");

        var quadList = new List<ModelQuad>();
        var boxList = new List<Box>();

        foreach (object elementObj in elements)
        {
            if (!(elementObj is Dictionary<string, object> el)) continue;
            if (!el.TryGetValue("from", out object fromObj) || !el.TryGetValue("to", out object toObj)) continue;

            Vector3 lo = Vec3(fromObj);
            Vector3 hi = Vec3(toObj);

            // Rotation de l'élément (un seul axe, comme dans Minecraft)
            bool rotated = false;
            string axis = "y";
            float angle = 0f;
            bool rescale = false;
            Vector3 origin = new Vector3(8f, 8f, 8f);

            if (el.TryGetValue("rotation", out object rotObj) && rotObj is Dictionary<string, object> rot)
            {
                if (rot.TryGetValue("angle", out object angObj)) angle = Num(angObj);
                if (rot.TryGetValue("axis", out object axObj) && axObj is string axisName) axis = axisName;
                if (rot.TryGetValue("origin", out object ogObj)) origin = Vec3(ogObj);
                if (rot.TryGetValue("rescale", out object rsObj) && rsObj is bool rescaleFlag) rescale = rescaleFlag;
                rotated = Mathf.Abs(angle) > 0.001f;
            }

            // Boîte de collision de l'élément : boîte englobante après rotation, limitée au bloc
            Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? lo.x : hi.x,
                    (i & 2) == 0 ? lo.y : hi.y,
                    (i & 4) == 0 ? lo.z : hi.z);
                if (rotated) corner = ApplyRotation(corner, axis, angle, origin, rescale);

                Vector3 cu = ToUnity(corner);
                bmin = Vector3.Min(bmin, cu);
                bmax = Vector3.Max(bmax, cu);
            }
            bmin = Vector3.Max(bmin, Vector3.zero);
            bmax = Vector3.Min(bmax, Vector3.one);
            Vector3 bsize = bmax - bmin;
            if (bsize.x > 0.001f && bsize.y > 0.001f && bsize.z > 0.001f) // ignore les plans sans épaisseur
                boxList.Add(new Box(bmin, bmax));

            if (!el.TryGetValue("faces", out object facesObj) || !(facesObj is Dictionary<string, object> faces)) continue;

            for (int f = 0; f < 6; f++)
            {
                if (!faces.TryGetValue(FaceNames[f], out object faceObj) || !(faceObj is Dictionary<string, object> face))
                    continue;

                int tile = ResolveTile(face, textures, tileByName, defaultTile);

                // UV : [u1, v1, u2, v2] ; par défaut, calculées d'après la position (comme Minecraft)
                float[] uv = DefaultUv(f, lo, hi);
                if (face.TryGetValue("uv", out object uvObj) && uvObj is List<object> uvList && uvList.Count >= 4)
                    uv = new[] { Num(uvList[0]), Num(uvList[1]), Num(uvList[2]), Num(uvList[3]) };

                // Rotation de la texture sur la face (0, 90, 180, 270)
                int uvRot = 0;
                if (face.TryGetValue("rotation", out object uvRotObj))
                    uvRot = ((Mathf.RoundToInt(Num(uvRotObj)) / 90) % 4 + 4) % 4;

                // Face masquée par un voisin opaque : "cullface" du fichier, sinon automatique
                // pour les faces posées contre le bord du bloc (élément non tourné)
                int cull = -1;
                if (face.TryGetValue("cullface", out object cullObj) && cullObj is string cullName)
                    cull = Array.IndexOf(FaceNames, cullName);
                else if (!rotated && IsFlush(f, lo, hi))
                    cull = f;

                // Sommets dans l'ordre haut-gauche, bas-gauche, bas-droite, haut-droite (vu de l'extérieur)
                Vector3[] bb = FaceCorners(f, lo, hi);
                var p = new Vector3[4];
                var t = new Vector2[4];
                for (int j = 0; j < 4; j++)
                {
                    Vector3 c = bb[j];
                    if (rotated) c = ApplyRotation(c, axis, angle, origin, rescale);
                    p[j] = ToUnity(c);

                    int corner = (j + uvRot) % 4;
                    float u = (corner == 0 || corner == 1) ? uv[0] : uv[2];
                    float v = (corner == 0 || corner == 3) ? uv[1] : uv[3];
                    // v de Blockbench va vers le bas, celui de l'atlas vers le haut
                    t[j] = BlockDatabase.TileUV(tile, u / texW, 1f - v / texH);
                }

                // Sens des triangles : on compare la normale géométrique à la direction attendue de la face.
                // (Le miroir Z inverse tous les sens, donc on ne peut pas le déduire d'une règle fixe.)
                Vector3 expected = BbDirs[f];
                if (rotated) expected = Rotate(expected, axis, angle);
                expected = new Vector3(expected.x, expected.y, -expected.z);

                Vector3 geometric = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
                if (geometric.sqrMagnitude < 1e-10f) continue; // face sans surface
                if (Vector3.Dot(geometric, expected) < 0f)
                {
                    (p[1], p[3]) = (p[3], p[1]);
                    (t[1], t[3]) = (t[3], t[1]);
                }

                quadList.Add(new ModelQuad
                {
                    p0 = p[0],
                    p1 = p[1],
                    p2 = p[2],
                    p3 = p[3],
                    uv0 = t[0],
                    uv1 = t[1],
                    uv2 = t[2],
                    uv3 = t[3],
                    normal = UnityDirs[f], // éclairage par direction de face, comme Minecraft
                    cullFace = cull
                });
            }
        }

        if (quadList.Count == 0) throw new FormatException("aucune face dans le modèle");

        quads = quadList.ToArray();
        boxes = boxList.ToArray();
    }

    // ------------------------------------------------------------------
    // Géométrie
    // ------------------------------------------------------------------

    // Les 4 coins d'une face, repère Blockbench (0..16), ordre : haut-gauche, bas-gauche, bas-droite, haut-droite
    // tels qu'on les voit de l'extérieur. C'est l'ordre auquel [u1,v1,u2,v2] se rapporte.
    static Vector3[] FaceCorners(int f, Vector3 a, Vector3 b)
    {
        float fx = a.x, fy = a.y, fz = a.z;
        float tx = b.x, ty = b.y, tz = b.z;

        switch (f)
        {
            case 0: return new[] { new Vector3(fx, ty, fz), new Vector3(fx, ty, tz), new Vector3(tx, ty, tz), new Vector3(tx, ty, fz) }; // up
            case 1: return new[] { new Vector3(fx, fy, tz), new Vector3(fx, fy, fz), new Vector3(tx, fy, fz), new Vector3(tx, fy, tz) }; // down
            case 2: return new[] { new Vector3(tx, ty, fz), new Vector3(tx, fy, fz), new Vector3(fx, fy, fz), new Vector3(fx, ty, fz) }; // north
            case 3: return new[] { new Vector3(fx, ty, tz), new Vector3(fx, fy, tz), new Vector3(tx, fy, tz), new Vector3(tx, ty, tz) }; // south
            case 4: return new[] { new Vector3(tx, ty, tz), new Vector3(tx, fy, tz), new Vector3(tx, fy, fz), new Vector3(tx, ty, fz) }; // east
            default: return new[] { new Vector3(fx, ty, fz), new Vector3(fx, fy, fz), new Vector3(fx, fy, tz), new Vector3(fx, ty, tz) }; // west
        }
    }

    // UV par défaut quand le fichier n'en donne pas (mêmes règles que Minecraft)
    static float[] DefaultUv(int f, Vector3 a, Vector3 b)
    {
        float fx = a.x, fy = a.y, fz = a.z;
        float tx = b.x, ty = b.y, tz = b.z;

        switch (f)
        {
            case 0: return new[] { fx, fz, tx, tz };                       // up
            case 1: return new[] { fx, 16f - tz, tx, 16f - fz };           // down
            case 2: return new[] { 16f - tx, 16f - ty, 16f - fx, 16f - fy }; // north
            case 3: return new[] { fx, 16f - ty, tx, 16f - fy };           // south
            case 4: return new[] { 16f - tz, 16f - ty, 16f - fz, 16f - fy }; // east
            default: return new[] { fz, 16f - ty, tz, 16f - fy };           // west
        }
    }

    // La face f (repère Blockbench, 0..16) touche-t-elle le bord du bloc ?
    static bool IsFlush(int f, Vector3 lo, Vector3 hi)
    {
        const float e = 0.001f;
        switch (f)
        {
            case 0: return hi.y >= 16f - e;    // up
            case 1: return lo.y <= e;        // down
            case 2: return lo.z <= e;        // north (-Z Blockbench = +Z Unity)
            case 3: return hi.z >= 16f - e;    // south
            case 4: return hi.x >= 16f - e;    // east
            default: return lo.x <= e;        // west
        }
    }

    static Vector3 ToUnity(Vector3 bb) => new Vector3(bb.x / 16f, bb.y / 16f, 1f - bb.z / 16f);

    // Rotation "règle de la main droite" autour d'un axe (repère Blockbench)
    static Vector3 Rotate(Vector3 v, string axis, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r), s = Mathf.Sin(r);

        switch (axis)
        {
            case "x": return new Vector3(v.x, v.y * c - v.z * s, v.y * s + v.z * c);
            case "y": return new Vector3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
            default: return new Vector3(v.x * c - v.y * s, v.x * s + v.y * c, v.z);
        }
    }

    static Vector3 ApplyRotation(Vector3 point, string axis, float degrees, Vector3 origin, bool rescale)
    {
        Vector3 v = Rotate(point - origin, axis, degrees);

        // "rescale" : compense le raccourcissement dû à la rotation sur les deux autres axes
        if (rescale)
        {
            float s = 1f / Mathf.Max(0.0001f, Mathf.Abs(Mathf.Cos(degrees * Mathf.Deg2Rad)));
            switch (axis)
            {
                case "x": v.y *= s; v.z *= s; break;
                case "y": v.x *= s; v.z *= s; break;
                default: v.x *= s; v.y *= s; break;
            }
        }

        return v + origin;
    }

    // ------------------------------------------------------------------
    // Textures et JSON
    // ------------------------------------------------------------------

    // "#0" -> textures["0"] -> "block/anvil" -> nom "anvil" -> tuile de l'atlas
    static int ResolveTile(Dictionary<string, object> face, Dictionary<string, string> textures,
                           Func<string, int> tileByName, int defaultTile)
    {
        if (!face.TryGetValue("texture", out object texRef) || !(texRef is string key)) return defaultTile;

        // Suit les références "#clé" jusqu'à un chemin de texture
        string path = key;
        for (int guard = 0; path.StartsWith("#") && guard < 8; guard++)
        {
            if (!textures.TryGetValue(path.Substring(1), out string next)) return defaultTile;
            path = next;
        }
        if (path.StartsWith("#")) return defaultTile;

        string name = path;
        int slash = name.LastIndexOfAny(new[] { '/', ':' });
        if (slash >= 0) name = name.Substring(slash + 1);
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);

        int tile = tileByName != null ? tileByName(name) : -1;
        return tile >= 0 ? tile : defaultTile;
    }

    static float Num(object o) => Convert.ToSingle(o, CultureInfo.InvariantCulture);

    static Vector3 Vec3(object o)
    {
        var l = o as List<object>;
        if (l == null || l.Count < 3) throw new FormatException("vecteur à 3 valeurs attendu");
        return new Vector3(Num(l[0]), Num(l[1]), Num(l[2]));
    }
}

// Parseur JSON minimal : objets -> Dictionary<string, object>, tableaux -> List<object>,
// nombres -> double, plus string / bool / null.
internal static class MiniJson
{
    public static object Parse(string json)
    {
        return new Parser(json.TrimStart('\uFEFF')).ParseValue();
    }

    sealed class Parser
    {
        readonly string s;
        int i;

        public Parser(string text) { s = text; }

        char Peek()
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) throw new FormatException("JSON tronqué");
            return s[i];
        }

        public object ParseValue()
        {
            switch (Peek())
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return ParseString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default: return ParseNumber();
            }
        }

        Dictionary<string, object> ParseObject()
        {
            var result = new Dictionary<string, object>();
            i++; // '{'
            if (Peek() == '}') { i++; return result; }

            while (true)
            {
                if (Peek() != '"') throw new FormatException($"clé attendue à la position {i}");
                string key = ParseString();

                if (Peek() != ':') throw new FormatException($"':' attendu à la position {i}");
                i++;

                result[key] = ParseValue();

                char c = Peek();
                i++;
                if (c == ',') continue;
                if (c == '}') return result;
                throw new FormatException($"',' ou '}}' attendu à la position {i - 1}");
            }
        }

        List<object> ParseArray()
        {
            var result = new List<object>();
            i++; // '['
            if (Peek() == ']') { i++; return result; }

            while (true)
            {
                result.Add(ParseValue());

                char c = Peek();
                i++;
                if (c == ',') continue;
                if (c == ']') return result;
                throw new FormatException($"',' ou ']' attendu à la position {i - 1}");
            }
        }

        string ParseString()
        {
            i++; // guillemet ouvrant
            var sb = new StringBuilder();

            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();

                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("séquence \\u tronquée");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
            throw new FormatException("chaîne non terminée");
        }

        double ParseNumber()
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException($"valeur inattendue '{s[i]}' à la position {i}");
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        void Expect(string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException($"'{word}' attendu à la position {i}");
            i += word.Length;
        }
    }
}