using System.Collections.Concurrent;
namespace SoundpadWeb;

/// <summary>
/// Which library sounds have no file on the host's PC (2.44, gate missingFiles). Checked in the background, four
/// paths at a time with 2 s per path: a path that does not answer in time (a sleeping network drive) is "unknown",
/// never missing. Results are kept per path and rescanned when the library changes and every 5 minutes; nothing
/// here ever runs on the playback path, which only reads <see cref="Missing"/>.
/// </summary>
internal sealed class MissingFiles(Func<string, bool>? exists = null) : IDisposable
{
    public const int Parallel = 4, PathTimeoutMs = 2000, RescanMs = 5 * 60 * 1000;
    private readonly Func<string, bool> exists = exists ?? File.Exists;
    private readonly ConcurrentDictionary<string, bool> missingByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource stop = new();
    private readonly object sync = new();
    private string? scannedVersion;
    private long scannedAt = long.MinValue / 2;
    private Task? running;
    private IReadOnlyDictionary<string, string> pending = new Dictionary<string, string>();
    private string? pendingVersion;
    /// <summary>The sound ids whose file is missing now (replaced, never changed in place).</summary>
    public IReadOnlySet<string> Missing { get; private set; } = new HashSet<string>();
    /// <summary>Rises whenever <see cref="Missing"/> changes.</summary>
    public long Revision { get; private set; }
    public event Action? Changed;
    /// <summary>
    /// Asks for a scan of these sounds (id → path) for this library version. Returns at once; a scan already running
    /// finishes first and the newest request runs after it. The same version is scanned again only after 5 minutes.
    /// </summary>
    public void Request(string version, IReadOnlyDictionary<string, string> paths, bool force = false)
    {
        lock (sync)
        {
            if (!force && version == scannedVersion && Environment.TickCount64 - scannedAt < RescanMs) return;
            pending = paths; pendingVersion = version;
            if (running is { IsCompleted: false }) return;
            running = Task.Run(Loop);
        }
    }
    /// <summary>Waits for the scans asked for so far (tests).</summary>
    public async Task Idle() { while (true) { Task? t; lock (sync) t = running; if (t == null || t.IsCompleted) return; await t; } }
    private async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            IReadOnlyDictionary<string, string> paths; string? version;
            lock (sync)
            {
                if (pendingVersion == null) { running = null; return; }
                paths = pending; version = pendingVersion; pendingVersion = null;
            }
            await Scan(paths);
            lock (sync) { scannedVersion = version; scannedAt = Environment.TickCount64; }
        }
    }
    private async Task Scan(IReadOnlyDictionary<string, string> paths)
    {
        using var slots = new SemaphoreSlim(Parallel);
        var checks = paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(async path =>
        {
            await slots.WaitAsync(stop.Token);
            try
            {
                var probe = Task.Run(() => exists(path));
                if (await Task.WhenAny(probe, Task.Delay(PathTimeoutMs, stop.Token)) != probe) return; // unknown: keep what was known
                bool present;
                try { present = await probe; } catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
                if (present) missingByPath.TryRemove(path, out _); else missingByPath[path] = true;
            }
            finally { slots.Release(); }
        }).ToArray();
        try { await Task.WhenAll(checks); } catch (OperationCanceledException) { return; }
        var next = paths.Where(p => missingByPath.ContainsKey(p.Value)).Select(p => p.Key).ToHashSet();
        if (!next.SetEquals(Missing)) { Missing = next; Revision++; Changed?.Invoke(); }
    }
    public void Dispose() { stop.Cancel(); }
}
