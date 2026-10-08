using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Les fours : un contenu (FurnaceData) par bloc four, qui continue de cuire tant que son chunk est chargé.
// (Partie de la classe World.)
public partial class World
{
    readonly Dictionary<Vector3Int, FurnaceData> furnaces = new Dictionary<Vector3Int, FurnaceData>();
    readonly List<Vector3Int> furnaceKeys = new List<Vector3Int>();
    bool furnacesLoaded;

    string FurnacePath => Path.Combine(Application.persistentDataPath, "furnaces.json");

    // Le contenu du four en p (créé s'il n'existe pas encore et que le bloc est bien un four)
    public FurnaceData GetFurnace(Vector3Int p, bool create)
    {
        EnsureFurnacesLoaded();
        if (furnaces.TryGetValue(p, out FurnaceData data)) return data;
        if (!create || !FurnaceIds.IsFurnace(GetBlock(p.x, p.y, p.z))) return null;

        data = new FurnaceData();
        furnaces[p] = data;
        return data;
    }

    void TickFurnaces(float dt)
    {
        EnsureFurnacesLoaded();
        if (furnaces.Count == 0) return;

        furnaceKeys.Clear();
        furnaceKeys.AddRange(furnaces.Keys);

        for (int i = 0; i < furnaceKeys.Count; i++)
        {
            Vector3Int p = furnaceKeys[i];
            if (!IsLoaded(p.x, p.z)) continue; // chunk déchargé : le four attend

            BlockType type = GetBlock(p.x, p.y, p.z);
            if (!FurnaceIds.IsFurnace(type)) { furnaces.Remove(p); continue; } // le bloc a disparu

            FurnaceData data = furnaces[p];
            data.Tick(dt);

            // Allumé / éteint : le bloc change (lumière et face avec les flammes), l'orientation est conservée
            int wanted = data.IsBurning ? FurnaceIds.Lit : FurnaceIds.Block;
            if ((int)type != wanted)
                SetBlockAndState(p.x, p.y, p.z, (BlockType)wanted, GetState(p.x, p.y, p.z), false);
        }
    }

    // Le four est détruit : son contenu tombe par terre
    void DropFurnaceContents(int x, int y, int z)
    {
        var p = new Vector3Int(x, y, z);
        if (!furnaces.TryGetValue(p, out FurnaceData data)) return;
        furnaces.Remove(p);

        var at = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        DropFurnaceStack(data.input, at);
        DropFurnaceStack(data.fuel, at);
        DropFurnaceStack(data.output, at);
    }

    void DropFurnaceStack(ItemStack stack, Vector3 at)
    {
        if (stack.IsEmpty) return;
        DropItem(stack, at, new Vector3(UnityEngine.Random.Range(-1f, 1f), 2.5f, UnityEngine.Random.Range(-1f, 1f)));
    }

    // ------------------------------------------------------------------
    // Sauvegarde (un petit fichier à côté de celui du monde)
    // ------------------------------------------------------------------

    [Serializable]
    class FurnaceSaveEntry
    {
        public Vector3Int pos;
        public FurnaceData data;
    }

    [Serializable]
    class FurnaceSaveFile
    {
        public List<FurnaceSaveEntry> entries = new List<FurnaceSaveEntry>();
    }

    void EnsureFurnacesLoaded()
    {
        if (furnacesLoaded) return;
        furnacesLoaded = true;
        if (!saveToDisk || !File.Exists(FurnacePath)) return;

        try
        {
            var file = JsonUtility.FromJson<FurnaceSaveFile>(File.ReadAllText(FurnacePath));
            if (file == null) return;
            foreach (FurnaceSaveEntry e in file.entries)
                if (e != null && e.data != null) furnaces[e.pos] = e.data;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Fours : sauvegarde illisible, ignorée (" + ex.Message + ")");
        }
    }

    void SaveFurnaces()
    {
        if (!furnacesLoaded) return;

        try
        {
            var file = new FurnaceSaveFile();
            foreach (var kv in furnaces)
                file.entries.Add(new FurnaceSaveEntry { pos = kv.Key, data = kv.Value });
            File.WriteAllText(FurnacePath, JsonUtility.ToJson(file));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Fours : sauvegarde impossible (" + ex.Message + ")");
        }
    }
}
