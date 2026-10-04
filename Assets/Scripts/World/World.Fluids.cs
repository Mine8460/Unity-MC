using System.Collections.Generic;
using UnityEngine;

// Niveaux de l'eau, rangés dans l'ÉTAT du bloc (ChunkData.state)
public static class WaterState
{
    public const byte Source = 0;    // bloc d'eau « plein » : ne s'épuise jamais (lacs, mers)
    public const byte MaxFlow = 7;   // 1 à 7 : eau qui coule, à 1 à 7 blocs de sa source
    public const byte Falling = 8;   // eau qui tombe (cascade)
}

// Écoulement de l'eau, avec les règles de Minecraft (Java) :
//   - l'eau tombe d'abord ; une eau qui coule ne s'étale sur les côtés que si elle ne peut plus descendre
//     (une source s'étale toujours) ;
//   - sur un sol plat, elle part vers le trou le plus proche (jusqu'à 4 blocs), sinon dans toutes les directions ;
//   - elle s'affaiblit d'un niveau par bloc : 7 blocs au plus autour d'une source ;
//   - une eau qui coule n'est plus alimentée ? Elle recule et disparaît ;
//   - deux sources voisines au-dessus d'un sol (ou d'une source) créent une nouvelle source.
// Seules les cases autour d'un changement sont recalculées : un lac immobile ne coûte rien.
// (Partie de la classe World.)
public partial class World
{
    [Header("Écoulement de l'eau")]
    [Tooltip("Temps entre deux mises à jour de l'eau (Minecraft : 0,25 s). L'eau avance d'un bloc par mise à jour.")]
    [SerializeField, Min(0.02f)] float fluidTickInterval = 0.25f;
    [Tooltip("Nombre max de cases d'eau recalculées par mise à jour (le reste attend la suivante)")]
    [SerializeField, Min(16)] int maxFluidUpdatesPerTick = 4096;

    readonly List<Vector3Int> fluidPending = new List<Vector3Int>();       // à traiter à la prochaine mise à jour
    readonly HashSet<Vector3Int> fluidPendingSet = new HashSet<Vector3Int>();
    readonly List<Vector3Int> fluidBatch = new List<Vector3Int>();
    readonly HashSet<Vector3Int> fluidChangedThisTick = new HashSet<Vector3Int>(); // l'eau avance d'UN bloc par mise à jour

    // Recherche du trou le plus proche (réutilisés : aucune allocation)
    readonly Queue<Vector3Int> holeQueue = new Queue<Vector3Int>();
    readonly Dictionary<Vector3Int, int> holeVisited = new Dictionary<Vector3Int, int>();
    readonly int[] spreadDistance = new int[4];

    float fluidTimer;

    const int HoleSearchRange = 4;   // comme Minecraft
    const int NoHole = 1000;

    static readonly Vector3Int[] HorizontalDirs =
    {
        new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
    };

    // ------------------------------------------------------------------
    // Programmation
    // ------------------------------------------------------------------

    // Un bloc a changé : lui et ses 6 voisins seront recalculés à la prochaine mise à jour de l'eau
    void ScheduleFluidAround(int x, int y, int z)
    {
        ScheduleFluid(x, y, z);
        ScheduleFluid(x + 1, y, z);
        ScheduleFluid(x - 1, y, z);
        ScheduleFluid(x, y + 1, z);
        ScheduleFluid(x, y - 1, z);
        ScheduleFluid(x, y, z + 1);
        ScheduleFluid(x, y, z - 1);
    }

    void ScheduleFluid(int x, int y, int z)
    {
        if ((uint)y >= (uint)Chunk.SizeY) return;
        var p = new Vector3Int(x, y, z);
        if (fluidPendingSet.Add(p)) fluidPending.Add(p);
    }

    void ProcessFluids(float dt)
    {
        if (fluidPending.Count == 0) { fluidTimer = 0f; return; }

        fluidTimer += dt;
        if (fluidTimer < fluidTickInterval) return;
        fluidTimer = 0f;

        // On prend les cases en attente ; celles programmées PENDANT ce traitement attendront la mise à jour
        // suivante : c'est ce qui fait avancer l'eau d'un bloc à la fois.
        int count = Mathf.Min(fluidPending.Count, maxFluidUpdatesPerTick);
        fluidBatch.Clear();
        for (int i = 0; i < count; i++)
        {
            fluidBatch.Add(fluidPending[i]);
            fluidPendingSet.Remove(fluidPending[i]);
        }
        fluidPending.RemoveRange(0, count);

        fluidChangedThisTick.Clear();
        foreach (Vector3Int p in fluidBatch)
            UpdateFluid(p.x, p.y, p.z);
        fluidChangedThisTick.Clear();

        // Les meshes touchés sont refaits UNE fois, en arrière-plan
        FlushDirtyChunks();
    }

    // Un bloc d'eau sur une bordure de chunk : le mesh du voisin dépend aussi de lui (faces, hauteur des coins)
    void MarkFluidDirty(Vector2Int coord, int lx, int lz)
    {
        MarkLightDirty(coord, lx, lz); // ce chunk et, sur une bordure, le voisin (remaillés en arrière-plan)

        bool edgeX = lx == 0 || lx == Chunk.SizeX - 1;
        bool edgeZ = lz == 0 || lz == Chunk.SizeZ - 1;
        if (edgeX && edgeZ)
        {
            int dx = lx == 0 ? -1 : 1;
            int dz = lz == 0 ? -1 : 1;
            lightDirty.Add(new Vector2Int(coord.x + dx, coord.y + dz)); // coin : le chunk en diagonale aussi
        }
    }

    // ------------------------------------------------------------------
    // Règles
    // ------------------------------------------------------------------

    // Change un bloc pendant l'écoulement (et retient qu'il a changé : il n'agira qu'à la mise à jour suivante)
    void SetFluidBlock(int x, int y, int z, BlockType type, byte state)
    {
        if (SetBlockAndState(x, y, z, type, state, true))
            fluidChangedThisTick.Add(new Vector3Int(x, y, z));
    }

    void UpdateFluid(int x, int y, int z)
    {
        if (!IsLoaded(x, z)) return;

        // Déjà modifiée pendant cette mise à jour : elle est reprogrammée, elle agira à la suivante
        // (sinon l'eau pourrait avancer de plusieurs blocs d'un coup selon l'ordre de traitement)
        if (fluidChangedThisTick.Contains(new Vector3Int(x, y, z))) return;
        if (GetBlock(x, y, z) != BlockType.Water) return; // une case vide est remplie par l'eau voisine

        byte state = GetState(x, y, z);

        if (state != WaterState.Source)
        {
            int wanted = FlowStateAt(x, y, z);

            if (wanted < 0)
            {
                // Plus rien ne l'alimente : l'eau recule
                SetFluidBlock(x, y, z, BlockType.Air, 0);
                return;
            }

            if (wanted != state)
            {
                // Son niveau change : elle s'étalera à la prochaine mise à jour, avec ce nouveau niveau
                SetFluidBlock(x, y, z, BlockType.Water, (byte)wanted);
                return;
            }
        }

        Spread(x, y, z, state);
    }

    // Niveau que devrait avoir une eau qui coule, d'après ce qui l'entoure. -1 = elle doit disparaître.
    int FlowStateAt(int x, int y, int z)
    {
        // De l'eau juste au-dessus : c'est une chute
        if (GetBlock(x, y + 1, z) == BlockType.Water) return WaterState.Falling;

        int best = int.MaxValue;
        int sources = 0;

        foreach (Vector3Int d in HorizontalDirs)
        {
            int nx = x + d.x, nz = z + d.z;
            if (GetBlock(nx, y, nz) != BlockType.Water) continue;

            byte ns = GetState(nx, y, nz);
            if (ns == WaterState.Source) sources++;

            // Une eau qui coule et peut encore descendre ne s'étale pas sur les côtés (une source, si)
            if (ns != WaterState.Source && IsWaterHole(nx, y - 1, nz)) continue;

            int level = ns == WaterState.Source || ns == WaterState.Falling ? 1 : ns + 1;
            if (level < best) best = level;
        }

        // Source infinie : entre deux sources, au-dessus d'un sol ou d'une source
        if (sources >= 2)
        {
            bool belowSolid = !IsWaterHole(x, y - 1, z);
            bool belowSource = GetBlock(x, y - 1, z) == BlockType.Water && GetState(x, y - 1, z) == WaterState.Source;
            if (belowSolid || belowSource) return WaterState.Source;
        }

        return best <= WaterState.MaxFlow ? best : -1;
    }

    // Règles de Minecraft (Java) :
    //   - si l'eau peut descendre, elle descend, et ne s'étale sur les côtés que si au moins 3 sources l'entourent
    //     (une source suspendue au-dessus du vide fait UNE cascade, pas cinq) ;
    //   - sinon, une source s'étale toujours ; une eau qui coule seulement si le dessous n'est pas un trou.
    void Spread(int x, int y, int z, byte state)
    {
        bool canFlowDown = IsWaterHole(x, y - 1, z) && CanFlowInto(x, y - 1, z, WaterState.Falling);

        if (canFlowDown)
        {
            SetFluidBlock(x, y - 1, z, BlockType.Water, WaterState.Falling);
            if (SourceNeighborCount(x, y, z) >= 3) SpreadToSides(x, y, z, state);
        }
        else if (state == WaterState.Source || !IsWaterHole(x, y - 1, z))
        {
            SpreadToSides(x, y, z, state);
        }
    }

    int SourceNeighborCount(int x, int y, int z)
    {
        int count = 0;
        foreach (Vector3Int d in HorizontalDirs)
            if (GetBlock(x + d.x, y, z + d.z) == BlockType.Water && GetState(x + d.x, y, z + d.z) == WaterState.Source)
                count++;
        return count;
    }

    void SpreadToSides(int x, int y, int z, byte state)
    {
        int next = state == WaterState.Source || state == WaterState.Falling ? 1 : state + 1;
        if (next > WaterState.MaxFlow) return;

        // Directions retenues : celles qui mènent au trou le plus proche (4 blocs max), sinon toutes.
        // Une direction déjà occupée par de l'eau qui coule COMPTE dans ce choix (comme Minecraft) : une fois
        // l'eau partie vers le trou, elle ne repart pas dans les autres directions.
        int best = int.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            Vector3Int d = HorizontalDirs[i];
            if (!CanPassThrough(x + d.x, y, z + d.z)) { spreadDistance[i] = -1; continue; }

            spreadDistance[i] = DistanceToHole(x + d.x, y, z + d.z, x, z);
            if (spreadDistance[i] < best) best = spreadDistance[i];
        }

        for (int i = 0; i < 4; i++)
        {
            if (spreadDistance[i] < 0 || spreadDistance[i] != best) continue;

            Vector3Int d = HorizontalDirs[i];
            if (CanFlowInto(x + d.x, y, z + d.z, (byte)next))
                SetFluidBlock(x + d.x, y, z + d.z, BlockType.Water, (byte)next);
        }
    }

    // La case peut-elle accueillir de l'eau qui tombe dedans ? (vide, remplaçable, ou déjà de l'eau)
    bool IsWaterHole(int x, int y, int z)
    {
        if ((uint)y >= (uint)Chunk.SizeY || !IsLoaded(x, z)) return false;
        return BlockDatabase.GetRef(GetBlock(x, y, z)).replaceable;
    }

    // L'eau de niveau `state` peut-elle couler dans cette case ?
    bool CanFlowInto(int x, int y, int z, byte state)
    {
        if ((uint)y >= (uint)Chunk.SizeY || !IsLoaded(x, z)) return false;

        BlockType type = GetBlock(x, y, z);
        if (type == BlockType.Water)
        {
            byte current = GetState(x, y, z);
            if (current == WaterState.Source) return false;              // on ne remplace jamais une source
            if (state == WaterState.Falling) return current != WaterState.Falling;
            return current != WaterState.Falling && state < current;     // seulement une eau plus faible
        }

        return BlockDatabase.GetRef(type).replaceable; // air, herbe haute... (une torche ou un bloc bloque l'eau)
    }

    // Nombre de blocs (à plat) jusqu'à la case la plus proche où l'eau pourrait tomber, en partant de (x, y, z),
    // sans repasser par la case d'origine de l'eau (fromX, fromZ)
    int DistanceToHole(int x, int y, int z, int fromX, int fromZ)
    {
        if (IsWaterHole(x, y - 1, z)) return 0;

        holeQueue.Clear();
        holeVisited.Clear();
        var start = new Vector3Int(x, y, z);
        holeQueue.Enqueue(start);
        holeVisited[start] = 0;
        holeVisited[new Vector3Int(fromX, y, fromZ)] = 0;

        while (holeQueue.Count > 0)
        {
            Vector3Int p = holeQueue.Dequeue();
            int dist = holeVisited[p];
            if (dist >= HoleSearchRange) continue;

            foreach (Vector3Int d in HorizontalDirs)
            {
                var q = p + d;
                if (holeVisited.ContainsKey(q)) continue;
                if (!CanPassThrough(q.x, q.y, q.z)) continue;

                if (IsWaterHole(q.x, q.y - 1, q.z)) return dist + 1;

                holeVisited[q] = dist + 1;
                holeQueue.Enqueue(q);
            }
        }

        return NoHole;
    }

    // L'eau pourrait-elle passer par là ? (vide, remplaçable, ou eau qui coule)
    bool CanPassThrough(int x, int y, int z)
    {
        if ((uint)y >= (uint)Chunk.SizeY || !IsLoaded(x, z)) return false;

        BlockType type = GetBlock(x, y, z);
        if (type == BlockType.Water) return GetState(x, y, z) != WaterState.Source;
        return BlockDatabase.GetRef(type).replaceable;
    }
}
