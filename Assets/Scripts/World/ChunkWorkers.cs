using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;
// UnityEngine a aussi un ThreadPriority (priorité de chargement des assets) : on veut celui des threads .NET
using ThreadPriority = System.Threading.ThreadPriority;

// Quelques threads secondaires qui exécutent les tâches lourdes (génération du terrain, lumière, maillage)
// pour que le thread principal ne gèle jamais. Les tâches sont des fonctions simples (Action) : elles ne
// doivent appeler AUCUNE API Unity qui touche un objet (GameObject, Mesh, Transform...).
public sealed class ChunkWorkers : IDisposable
{
    readonly ConcurrentQueue<Action> jobs = new ConcurrentQueue<Action>();
    readonly SemaphoreSlim signal = new SemaphoreSlim(0);
    readonly Thread[] threads;
    volatile bool stopped;

    public int ThreadCount => threads.Length;

    public ChunkWorkers(int count)
    {
        threads = new Thread[Math.Max(1, count)];

        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(Loop)
            {
                // "Background" : ces threads ne bloquent jamais la fermeture du jeu
                IsBackground = true,
                Name = "Chunk worker " + i,
                // Un peu moins prioritaires que le jeu lui-même : le rendu passe avant la génération
                Priority = ThreadPriority.BelowNormal,
            };
            threads[i].Start();
        }
    }

    // Ajoute une tâche. Elles sont exécutées dans l'ordre d'arrivée.
    public void Enqueue(Action job)
    {
        if (stopped) return;
        jobs.Enqueue(job);
        signal.Release();
    }

    void Loop()
    {
        while (true)
        {
            signal.Wait();
            if (stopped) return;
            if (!jobs.TryDequeue(out Action job)) continue;

            try
            {
                job();
            }
            catch (Exception e)
            {
                // Une exception non rattrapée dans un thread ferait planter tout l'éditeur : on la signale seulement
                Debug.LogException(e);
            }
        }
    }

    // Arrête les threads. À appeler quand le monde est détruit (sinon ils survivent à l'arrêt du mode Play).
    public void Dispose()
    {
        stopped = true;
        signal.Release(threads.Length);

        for (int i = 0; i < threads.Length; i++)
            threads[i].Join(250);
    }
}
