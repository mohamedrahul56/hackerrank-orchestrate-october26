using System.Collections.Concurrent;

namespace Auditor.Evidence;

/// <summary>
/// Read many screenshots concurrently, apply the readings in the original order.
/// The model calls are the only slow, I/O-bound part of reading evidence. <see cref="Prefetch"/> runs them in a bounded pool and returns a reader with the same interface as the one
/// it wraps; <c>Load.LoadEvidence</c> then walks the manifest sequentially as before and every <c>LoadImage</c> call gets its reading (or re-throws its exception) from the prefetched
/// results. So observations, warnings and everything derived from them come out in manifest order whatever order the replies arrived in: concurrency changes the wall time, never the output.
/// </summary>
public static class ImageJobs
{
    public const int MaxWorkers = 16;

    public static IImageReader Prefetch(IImageReader reader, IReadOnlyList<string> paths, string system, string instruction, Dictionary<string, object?> schema, string promptVersion, int workers)
    {
        workers = Math.Max(1, Math.Min(Math.Min(workers, MaxWorkers), Math.Max(paths.Count, 1)));
        if (workers == 1 || paths.Count < 2) return reader;                      // nothing to overlap: keep the plain sequential path
        var unique = paths.Distinct(StringComparer.Ordinal).ToList();
        var results = new ConcurrentDictionary<string, Outcome>(StringComparer.Ordinal);
        Parallel.ForEach(unique, new ParallelOptions { MaxDegreeOfParallelism = workers }, p => results[p] = One(reader, p, system, instruction, schema, promptVersion));
        return new Replay(results, reader, system, instruction, schema, promptVersion);
    }

    private sealed record Outcome(Dictionary<string, object?>? Value, Exception? Error);

    private static Outcome One(IImageReader reader, string path, string system, string instruction, Dictionary<string, object?> schema, string promptVersion)
    {
        try { return new Outcome(reader.Read(path, system, instruction, schema, promptVersion), null); }
        catch (Exception e) { return new Outcome(null, e); }                      // re-thrown in manifest order by the wrapper below
    }

    private sealed class Replay : IImageReader
    {
        private readonly ConcurrentDictionary<string, Outcome> results;
        private readonly IImageReader inner;
        private readonly string system, instruction, promptVersion;
        private readonly Dictionary<string, object?> schema;

        public Replay(ConcurrentDictionary<string, Outcome> results, IImageReader inner, string system, string instruction, Dictionary<string, object?> schema, string promptVersion)
        {
            this.results = results; this.inner = inner; this.system = system; this.instruction = instruction; this.schema = schema; this.promptVersion = promptVersion;
        }

        public string ObsSource => inner.ObsSource;                              // the source tag of the observations travels with the reader

        public Dictionary<string, object?> Read(string path, string system, string instruction, Dictionary<string, object?> schema, string promptVersion)
        {
            var outcome = results.TryGetValue(path, out var o) ? o : One(inner, path, this.system, this.instruction, this.schema, this.promptVersion);
            if (outcome.Error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome.Error).Throw();
            return outcome.Value!;
        }
    }
}
