// The engine's parallel loop (WorkerPool, not to clash with System.Threading.ThreadPool): persistent worker threads
// that split an index range, the caller working the range too.
// Spartan's counterpart is ThreadPool::ParallelLoop (source/core/ThreadPool.cpp:176-240 @ 4c07baa): hardware_concurrency
// workers, the range cut into one contiguous share per worker, the caller running the last share, nested loops inline.
// Deviations:
// - Workers take blocks of the range from a shared counter instead of a fixed share each: chunks differ in size and
//   in work (empty, culled whole, partly dirty), so a fixed split leaves cores idle behind the slowest share.
// - A loop is a ParallelJob object the caller keeps, no std::function/closure: Parallel.For allocated about 14 KB per
//   tick on the world's three loops; a cached job allocates nothing.
// - Processor count - 1 workers (spartan: min(2 * cores / 2, cores / 2 + 4)), the caller is the last one.
using System.Runtime.ExceptionServices;

namespace Phalanx;

/// <summary>A body for <see cref="WorkerPool.ParallelLoop"/>: <see cref="Execute"/> runs once per index, concurrently.</summary>
public abstract class ParallelJob
{
    public abstract void Execute(int index);
}

public static class WorkerPool
{
    private static readonly object s_mutex = new();
    private static Thread[] s_threads = Array.Empty<Thread>();
    private static readonly SemaphoreSlim s_wake = new(0);
    private static readonly ManualResetEventSlim s_done = new(false);
    private static ParallelJob? s_job;
    private static int s_total, s_next, s_block, s_active;
    private static ExceptionDispatchInfo? s_failure;
    [ThreadStatic] private static bool t_inside;

    /// <summary>The worker threads plus the caller.</summary>
    public static int ThreadCount => s_threads.Length + 1;

    private static void EnsureStarted()
    {
        if (s_threads.Length > 0 || Environment.ProcessorCount < 2)
            return;
        lock (s_mutex)
        {
            if (s_threads.Length > 0)
                return;
            var threads = new Thread[Environment.ProcessorCount - 1];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"phalanx worker {i}" };
                threads[i].Start();
            }
            s_threads = threads;
        }
    }

    private static void WorkerLoop()
    {
        t_inside = true;
        while (true)
        {
            s_wake.Wait();
            Work();
            if (Interlocked.Decrement(ref s_active) == 0)
                s_done.Set();
        }
    }

    private static void Work()
    {
        ParallelJob job = s_job!;
        int total = s_total, block = s_block;
        while (true)
        {
            int start = Interlocked.Add(ref s_next, block) - block;
            if (start >= total)
                return;
            int end = System.Math.Min(start + block, total);
            try
            {
                for (int i = start; i < end; i++)
                    job.Execute(i);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref s_failure, ExceptionDispatchInfo.Capture(e), null);
                Interlocked.Exchange(ref s_next, total); // the rest is skipped
                return;
            }
        }
    }

    /// <summary>Runs job.Execute(i) for every i in [0, total) on the workers and the calling thread; returns when all
    /// are done and rethrows the first exception. A loop from inside a loop, or with one index, runs inline. Loops
    /// from different threads are serialized.</summary>
    public static void ParallelLoop(ParallelJob job, int total)
    {
        if (total <= 0)
            return;
        EnsureStarted();
        if (total == 1 || t_inside || s_threads.Length == 0)
        {
            for (int i = 0; i < total; i++)
                job.Execute(i);
            return;
        }
        lock (s_mutex)
        {
            int workers = System.Math.Min(s_threads.Length, total - 1);
            s_job     = job;
            s_total   = total;
            s_next    = 0;
            s_block   = System.Math.Max(1, total / (ThreadCount * 4)); // a few blocks per thread for the balance
            s_failure = null;
            s_active  = workers;
            s_done.Reset();
            s_wake.Release(workers);
            t_inside = true;
            try
            {
                Work();
            }
            finally
            {
                t_inside = false;
            }
            s_done.Wait();
            s_job = null;
            s_failure?.Throw();
        }
    }
}
