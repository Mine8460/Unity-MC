using System.Collections.Generic;
using UnityEngine;

// Simulation de la redstone, avec les règles de Minecraft Java.
//
//   Le SIGNAL va de 0 à 15. Un fil (poussière) le porte et en perd 1 par bloc ; les sources le donnent à 15 :
//   levier, bouton, bloc de redstone, torche, répéteur.
//   La PUISSANCE FORTE traverse un bloc plein : un levier collé sur un bloc, ou une torche dessous, alimente ce bloc,
//   qui alimente à son tour les composants voisins. La puissance FAIBLE (celle d'un fil qui pointe vers un bloc) ne
//   passe pas d'un fil à l'autre à travers un bloc.
//   Les fils se mettent à jour d'un coup (tout le réseau relié) ; les torches, répéteurs et lampes ont leur DÉLAI :
//   tics de jeu de 0,05 s (torche : 2, répéteur : 2 à 8, bouton : 20, lampe qui s'éteint : 4).
//
// Seules les cases autour d'un changement sont recalculées : un circuit au repos ne coûte rien.
// (Partie de la classe World.)
public partial class World : IBlockView
{
    [Header("Redstone")]
    [SerializeField] bool redstoneEnabled = true;

    const float RedstoneTickSeconds = 0.05f;
    const int RedstonePassLimit = 64;

    readonly HashSet<Vector3Int> rsDirty = new HashSet<Vector3Int>();       // cases à recalculer
    readonly List<Vector3Int> rsBatch = new List<Vector3Int>();
    readonly HashSet<Vector3Int> rsWireDone = new HashSet<Vector3Int>();    // fils déjà recalculés pendant cette passe
    readonly Dictionary<Vector3Int, int> rsScheduled = new Dictionary<Vector3Int, int>(); // case -> tic d'exécution
    readonly List<Vector3Int> rsDue = new List<Vector3Int>();
    readonly int[] rsConn = new int[4];

    // Réseau de fils en cours de calcul (réutilisé : aucune allocation)
    readonly List<Vector3Int> rsComp = new List<Vector3Int>();
    readonly Dictionary<Vector3Int, int> rsCompIndex = new Dictionary<Vector3Int, int>();
    int[] rsLevel = new int[256];
    int[] rsAdj = new int[256 * 4];

    int rsTick;
    float rsAccumulator;

    // Pour les règles partagées avec le mailleur (Redstone.WireShape...)
    BlockType IBlockView.TypeAt(int x, int y, int z) => GetBlock(x, y, z);
    byte IBlockView.StateAt(int x, int y, int z) => GetState(x, y, z);

    // Un composant de redstone est-il encore à traiter ? (utile pour les tests)
    public bool RedstoneIdle => rsDirty.Count == 0 && rsScheduled.Count == 0;

    // ------------------------------------------------------------------
    // Boucle
    // ------------------------------------------------------------------

    void ProcessRedstone(float dt)
    {
        if (!redstoneEnabled) return;

        if (RedstoneIdle)
        {
            rsAccumulator = 0f;
            return;
        }

        rsAccumulator += dt;
        int steps = 0;
        while (rsAccumulator >= RedstoneTickSeconds && steps < 4)
        {
            rsAccumulator -= RedstoneTickSeconds;
            steps++;
            RedstoneTick();
        }
        if (steps == 4) rsAccumulator = 0f; // en retard : on ne rattrape pas, pour ne pas s'emballer
    }

    // Un tic de jeu : les actions programmées arrivées à échéance, puis les mises à jour en cascade
    void RedstoneTick()
    {
        rsTick++;

        rsDue.Clear();
        foreach (var kv in rsScheduled)
            if (kv.Value <= rsTick) rsDue.Add(kv.Key);
        foreach (Vector3Int p in rsDue)
        {
            rsScheduled.Remove(p);
            ExecuteScheduled(p);
        }

        ProcessRedstoneDirty();
        FlushDirtyChunks();
    }

    // Recalcule les cases signalées, passe après passe, jusqu'à ce que tout soit stable (ou la limite atteinte :
    // un circuit instantané qui boucle continuera au tic suivant au lieu de figer le jeu)
    void ProcessRedstoneDirty()
    {
        for (int pass = 0; pass < RedstonePassLimit && rsDirty.Count > 0; pass++)
        {
            rsBatch.Clear();
            rsBatch.AddRange(rsDirty);
            rsDirty.Clear();
            rsWireDone.Clear();

            foreach (Vector3Int p in rsBatch)
                EvaluateRedstone(p);
        }
    }

    // ------------------------------------------------------------------
    // Notifications
    // ------------------------------------------------------------------

    // Un bloc a changé : il est recalculé, ainsi que ses voisins, et les voisins des blocs pleins voisins
    // (un bloc plein alimenté par un levier alimente à son tour ce qui l'entoure)
    void NotifyRedstone(int x, int y, int z)
    {
        RsMark(x, y, z);
        foreach (Vector3Int d in Redstone.AllDirs)
        {
            int nx = x + d.x, ny = y + d.y, nz = z + d.z;
            RsMark(nx, ny, nz);

            if ((uint)ny < (uint)Chunk.SizeY && Redstone.IsSolid(GetBlock(nx, ny, nz)))
                foreach (Vector3Int e in Redstone.AllDirs)
                    RsMark(nx + e.x, ny + e.y, nz + e.z);
        }
    }

    void RsMark(int x, int y, int z)
    {
        if ((uint)y < (uint)Chunk.SizeY) rsDirty.Add(new Vector3Int(x, y, z));
    }

    void ScheduleRedstone(Vector3Int p, int delayTicks)
    {
        if (!rsScheduled.ContainsKey(p)) rsScheduled[p] = rsTick + delayTicks;
    }

    // Modifie un bloc de redstone. Les meshes sont refaits en groupe à la fin du tic (comme l'eau).
    bool SetRs(Vector3Int p, BlockType type, byte state)
    {
        return SetBlockAndState(p.x, p.y, p.z, type, state, true);
    }

    // ------------------------------------------------------------------
    // Puissance
    // ------------------------------------------------------------------

    // Puissance FAIBLE que le bloc en n donne à son voisin dans la direction toRecv
    int WeakPower(Vector3Int n, BlockType t, byte state, Vector3Int toRecv, bool wiresGive)
    {
        if ((int)t == Redstone.Block) return Redstone.MaxPower;
        if (Redstone.IsLever(t, out bool on, out _)) return on ? Redstone.MaxPower : 0;
        if (Redstone.IsButton(t, out bool pressed, out _)) return pressed ? Redstone.MaxPower : 0;

        if (Redstone.IsTorch(t, out bool lit, out int orient))
            return lit && Redstone.AttachDirs[orient] != toRecv ? Redstone.MaxPower : 0; // jamais vers le bloc qui la porte

        if (Redstone.IsRepeater(t))
            return Redstone.RepeaterPowered(state) && Redstone.Horizontal[Redstone.RepeaterFacing(state)] == toRecv ? Redstone.MaxPower : 0;

        if (wiresGive && Redstone.IsDust(t)) return DustPowerToward(n, state, toRecv);
        return 0;
    }

    // Puissance FORTE : celle qui traverse un bloc plein
    int StrongPower(Vector3Int m, BlockType t, byte state, Vector3Int toRecv, bool wiresGive)
    {
        if (Redstone.IsLever(t, out bool on, out int leverOrient))
            return on && Redstone.AttachDirs[leverOrient] == toRecv ? Redstone.MaxPower : 0;   // le bloc où il est collé

        if (Redstone.IsButton(t, out bool pressed, out int buttonOrient))
            return pressed && Redstone.AttachDirs[buttonOrient] == toRecv ? Redstone.MaxPower : 0;

        if (Redstone.IsTorch(t, out bool lit, out _))
            return lit && toRecv.y > 0 ? Redstone.MaxPower : 0;                                 // le bloc au-dessus

        if (Redstone.IsRepeater(t))
            return Redstone.RepeaterPowered(state) && Redstone.Horizontal[Redstone.RepeaterFacing(state)] == toRecv ? Redstone.MaxPower : 0;

        if (wiresGive && Redstone.IsDust(t)) return DustPowerToward(m, state, toRecv);
        return 0;
    }

    // Un fil donne son niveau au bloc DESSOUS et à ceux vers lesquels il pointe, jamais au bloc dessus
    int DustPowerToward(Vector3Int n, byte level, Vector3Int toRecv)
    {
        if (level == 0 || toRecv.y > 0) return 0;
        if (toRecv.y < 0) return level;

        Redstone.WireShape(this, n.x, n.y, n.z, rsConn);
        for (int i = 0; i < 4; i++)
            if (Redstone.Horizontal[i] == toRecv) return rsConn[i] > 0 ? level : 0;
        return 0;
    }

    // Puissance reçue par un bloc plein de la part de TOUS ses voisins en force
    int ReceivedStrong(Vector3Int n, bool wiresGive)
    {
        int best = 0;
        foreach (Vector3Int d in Redstone.AllDirs)
        {
            var m = new Vector3Int(n.x + d.x, n.y + d.y, n.z + d.z);
            int p = StrongPower(m, GetBlock(m.x, m.y, m.z), GetState(m.x, m.y, m.z), -d, wiresGive);
            if (p > best) best = p;
        }
        return best;
    }

    // Puissance que le bloc en n donne à son voisin dans la direction toRecv. Un bloc plein donne aussi
    // ce qu'il reçoit en force.
    int EmittedBy(Vector3Int n, Vector3Int toRecv, bool wiresGive)
    {
        BlockType t = GetBlock(n.x, n.y, n.z);
        int power = WeakPower(n, t, GetState(n.x, n.y, n.z), toRecv, wiresGive);
        if (Redstone.IsSolid(t)) power = Mathf.Max(power, ReceivedStrong(n, wiresGive));
        return power;
    }

    // Puissance reçue par la case `receiver` depuis son voisin dans la direction d
    int PowerInto(Vector3Int receiver, Vector3Int d, bool wiresGive)
    {
        return EmittedBy(receiver + d, -d, wiresGive);
    }

    // Puissance reçue par une case : le plus fort de ses 6 voisins
    int ReceivedPower(Vector3Int receiver, bool wiresGive)
    {
        int best = 0;
        foreach (Vector3Int d in Redstone.AllDirs)
        {
            int p = PowerInto(receiver, d, wiresGive);
            if (p > best)
            {
                best = p;
                if (best >= Redstone.MaxPower) break;
            }
        }
        return best;
    }

    // Puissance reçue par un répéteur sur son côté d'entrée (derrière lui)
    int RepeaterInput(Vector3Int p, int facing)
    {
        Vector3Int back = -Redstone.Horizontal[facing];
        int power = PowerInto(p, back, true);
        if (power >= Redstone.MaxPower) return power;

        var b = new Vector3Int(p.x + back.x, p.y + back.y, p.z + back.z);
        if (Redstone.IsDust(GetBlock(b.x, b.y, b.z))) power = Mathf.Max(power, GetState(b.x, b.y, b.z));
        return power;
    }

    // ------------------------------------------------------------------
    // Évaluation des composants
    // ------------------------------------------------------------------

    void EvaluateRedstone(Vector3Int p)
    {
        BlockType t = GetBlock(p.x, p.y, p.z);

        if (Redstone.IsDust(t))
        {
            if (!rsWireDone.Contains(p)) UpdateWireNetwork(p);
        }
        else if (Redstone.IsTorch(t, out bool lit, out int orient))
        {
            // Allumée et son bloc est alimenté, ou éteinte et il ne l'est plus : elle va changer (en 2 tics)
            if (lit == TorchShouldBeOff(p, orient)) ScheduleRedstone(p, 2);
        }
        else if (Redstone.IsRepeater(t))
        {
            byte state = GetState(p.x, p.y, p.z);
            bool input = RepeaterInput(p, Redstone.RepeaterFacing(state)) > 0;
            if (Redstone.RepeaterPowered(state) != input)
                ScheduleRedstone(p, (Redstone.RepeaterDelay(state) + 1) * 2);
        }
        else if (Redstone.IsLamp(t, out bool lampLit))
        {
            bool powered = ReceivedPower(p, true) > 0;
            if (!lampLit && powered) SetRs(p, (BlockType)Redstone.LampLit, 0);      // s'allume tout de suite
            else if (lampLit && !powered) ScheduleRedstone(p, 4);                     // s'éteint avec un petit délai
        }
    }

    // Le bloc qui porte la torche est-il alimenté en force (levier, fil qui pointe vers lui...) ? Alors elle s'éteint.
    bool TorchShouldBeOff(Vector3Int p, int orient)
    {
        Vector3Int toward = Redstone.AttachDirs[orient];
        var attached = new Vector3Int(p.x + toward.x, p.y + toward.y, p.z + toward.z);
        return EmittedBy(attached, -toward, true) > 0;
    }

    // Action programmée arrivée à échéance
    void ExecuteScheduled(Vector3Int p)
    {
        BlockType t = GetBlock(p.x, p.y, p.z);

        if (Redstone.IsTorch(t, out bool lit, out int orient))
        {
            bool off = TorchShouldBeOff(p, orient);
            if (lit && off) SetRs(p, Redstone.Torch(false, orient), 0);
            else if (!lit && !off) SetRs(p, Redstone.Torch(true, orient), 0);
        }
        else if (Redstone.IsRepeater(t))
        {
            byte state = GetState(p.x, p.y, p.z);
            int facing = Redstone.RepeaterFacing(state), delay = Redstone.RepeaterDelay(state);
            bool input = RepeaterInput(p, facing) > 0;

            if (Redstone.RepeaterPowered(state) && !input)
            {
                SetRs(p, t, Redstone.RepeaterState(facing, delay, false));
            }
            else if (!Redstone.RepeaterPowered(state))
            {
                // Une fois allumé, il reste allumé pendant tout son délai, même si le signal repart déjà
                SetRs(p, t, Redstone.RepeaterState(facing, delay, true));
                if (!input) ScheduleRedstone(p, (delay + 1) * 2);
            }
        }
        else if (Redstone.IsLamp(t, out bool lampLit) && lampLit)
        {
            if (ReceivedPower(p, true) == 0) SetRs(p, (BlockType)Redstone.Lamp, 0);
        }
        else if (Redstone.IsButton(t, out bool pressed, out int buttonOrient) && pressed)
        {
            SetRs(p, Redstone.Button(false, buttonOrient), 0);
        }
    }

    // ------------------------------------------------------------------
    // Réseau de fils
    // ------------------------------------------------------------------

    // Recalcule d'un coup tous les fils reliés à `start` : chaque fil reçoit le signal des sources voisines
    // (levier, torche...), puis le signal se propage de fil en fil en perdant 1 niveau par bloc.
    void UpdateWireNetwork(Vector3Int start)
    {
        // 1) Le réseau : tous les fils reliés (à plat, en montée ou en descente)
        rsComp.Clear();
        rsCompIndex.Clear();
        rsComp.Add(start);
        rsCompIndex[start] = 0;

        for (int i = 0; i < rsComp.Count; i++)
        {
            Vector3Int w = rsComp[i];
            for (int dir = 0; dir < 4; dir++)
            {
                Redstone.Link(this, w.x, w.y, w.z, dir, out int ty, out bool toWire);
                if (!toWire) continue;

                Vector3Int h = Redstone.Horizontal[dir];
                var q = new Vector3Int(w.x + h.x, ty, w.z + h.z);
                if (!rsCompIndex.ContainsKey(q))
                {
                    rsCompIndex[q] = rsComp.Count;
                    rsComp.Add(q);
                }
            }
        }

        int n = rsComp.Count;
        if (rsLevel.Length < n)
        {
            rsLevel = new int[n * 2];
            rsAdj = new int[n * 2 * 4];
        }

        // 2) Les liens de chaque fil, et la puissance qu'il reçoit des sources (sans compter les autres fils)
        for (int i = 0; i < n; i++)
        {
            Vector3Int w = rsComp[i];
            for (int dir = 0; dir < 4; dir++)
            {
                Redstone.Link(this, w.x, w.y, w.z, dir, out int ty, out bool toWire);
                int j = -1;
                if (toWire)
                {
                    Vector3Int h = Redstone.Horizontal[dir];
                    rsCompIndex.TryGetValue(new Vector3Int(w.x + h.x, ty, w.z + h.z), out j);
                }
                rsAdj[i * 4 + dir] = toWire ? j : -1;
            }
            rsLevel[i] = ReceivedPower(w, false);
        }

        // 3) Propagation : du niveau 15 vers le bas, un fil donne (son niveau - 1) à ses voisins
        for (int level = Redstone.MaxPower; level >= 2; level--)
        {
            for (int i = 0; i < n; i++)
            {
                if (rsLevel[i] != level) continue;
                for (int dir = 0; dir < 4; dir++)
                {
                    int j = rsAdj[i * 4 + dir];
                    if (j >= 0 && rsLevel[j] < level - 1) rsLevel[j] = level - 1;
                }
            }
        }

        // 4) Application
        for (int i = 0; i < n; i++)
        {
            Vector3Int w = rsComp[i];
            rsWireDone.Add(w);
            if (GetState(w.x, w.y, w.z) != rsLevel[i])
                SetRs(w, (BlockType)Redstone.Dust, (byte)rsLevel[i]);
        }
    }

    // ------------------------------------------------------------------
    // Interactions du joueur
    // ------------------------------------------------------------------

    // Clic droit sur un levier (il bascule), un bouton (il s'enfonce une seconde) ou un répéteur (son délai
    // passe à l'étape suivante). Retourne true si le bloc visé est l'un d'eux.
    public bool TryInteract(Vector3Int p)
    {
        BlockType t = GetBlock(p.x, p.y, p.z);

        if (Redstone.IsLever(t, out bool on, out int leverOrient))
        {
            SetRs(p, Redstone.Lever(!on, leverOrient), 0);
        }
        else if (Redstone.IsButton(t, out bool pressed, out int buttonOrient))
        {
            if (!pressed)
            {
                SetRs(p, Redstone.Button(true, buttonOrient), 0);
                ScheduleRedstone(p, 20); // reste enfoncé 1 seconde
            }
        }
        else if (Redstone.IsRepeater(t))
        {
            byte state = GetState(p.x, p.y, p.z);
            int delay = (Redstone.RepeaterDelay(state) + 1) & 3;
            SetRs(p, t, Redstone.RepeaterState(Redstone.RepeaterFacing(state), delay, Redstone.RepeaterPowered(state)));
        }
        else
        {
            return false;
        }

        FlushDirtyChunks();
        return true;
    }
}
