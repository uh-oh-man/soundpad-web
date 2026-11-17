namespace SoundpadWeb;

/// <summary>How the updater is set up for this launch (2.44). Program.cs fills it from the build and the command line.</summary>
public sealed class UpdateOptions
{
    /// <summary>Null for builds that do not update themselves (dev, forks, the test harness): state "unsupported".</summary>
    public UpdateSource? Source { get; init; }
    public required string Current { get; init; }
    /// <summary>The running SoundpadWeb.exe or SoundpadWeb-&lt;version&gt;.exe (2.48, see UpdateNames); null when there is
    /// none to replace (the harness, tests, any other name).</summary>
    public string? ExePath { get; init; }
    public string? DataDir { get; init; }
    /// <summary>Tests: a canned HTTP handler instead of the network.</summary>
    public HttpMessageHandler? Handler { get; init; }
    public Func<string, CancellationToken, Task<(int Exit, string Output)>> Probe { get; init; } = UpdateVerifier.Probe;
    /// <summary>Why the EXE folder cannot take an update (not writable, not NTFS), or null.</summary>
    public Func<string, string?> LocationProblem { get; init; } = UpdateLocation.Problem;
    public TimeSpan LaunchDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Whether the EXE's folder can take an update: rename-while-running is proven only on local NTFS.</summary>
public static class UpdateLocation
{
    public static string? Problem(string exePath)
    {
        var folder = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal)) return UpdateReasons.NotNtfs;
        try { if (new DriveInfo(Path.GetPathRoot(folder)!).DriveFormat != "NTFS") return UpdateReasons.NotNtfs; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return UpdateReasons.NotNtfs; }
        var probe = Path.Combine(folder, ".SoundpadWeb.write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllBytes(probe, [1]); File.Delete(probe); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return UpdateReasons.NotWritable; }
    }
}

/// <summary>
/// The self-update state machine (2.44): checking for a newer release, downloading and verifying it, and the state the
/// host's pages show (<see cref="Status"/>). Installing is the Bridge's and Program.cs's (BridgeUpdates.cs, UpdateSwap).
/// One operation at a time; a check or download asked for while another runs is ignored. Never installs by itself.
/// </summary>
public sealed class Updater : IDisposable
{
    private readonly UpdateOptions options;
    private readonly ReleaseClient? client;
    private readonly SemaphoreSlim busy = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly object sync = new();
    private UpdateStatus status;
    private ReleaseInfo? release;
    private bool offered, launchChecked;
    public event Action? Changed;
    /// <summary>One-line events for the companion log (never tokens or paths outside the EXE folder).</summary>
    public event Action<string>? Log;
    /// <summary>The verified download, once <see cref="UpdateStatus.State"/> is "ready".</summary>
    public string? DownloadPath { get; private set; }
    /// <summary>The published SHA-256 the download matched when it was checked.</summary>
    private string? verifiedHash;
    /// <summary>Test seam (harness verb update-state): the state was set by hand, so installing skips the files.</summary>
    public bool Forced { get; private set; }
    public UpdateSettingsFile? Settings { get; }

    public Updater(UpdateOptions options)
    {
        this.options = options;
        if (options.DataDir != null) Settings = new(Path.Combine(options.DataDir, "update-settings.v1.json"));
        if (options.Source == null) status = new("unsupported", options.Current, Reason: UpdateReasons.NoRepository, Error: "This build doesn't update itself.");
        else if (options.ExePath == null) status = new("unsupported", options.Current, Reason: UpdateReasons.NoExe, Error: "This build doesn't update itself.", NotesUrl: options.Source.ReleasesPage);
        else { status = new("idle", options.Current); client = new(options.Source, options.Handler); }
    }

    public UpdateStatus Status { get { lock (sync) return offered ? status with { Offered = true } : status; } }
    public bool CheckAtLaunch => Settings?.CheckAtLaunch() ?? true;
    private Version CurrentVersion => UpdateVersion.ParsePlain(options.Current) ?? new(0, 0, 0);

    private void Set(UpdateStatus next) { lock (sync) status = next; Changed?.Invoke(); }

    /// <summary>The host said Later (or dismissed the notice): it does not come back in this launch.</summary>
    public void Later() { lock (sync) offered = true; Changed?.Invoke(); }

    public void SetCheckAtLaunch(bool check)
    {
        if (Settings == null) throw new InvalidDataException("Update settings are not available here.");
        try { Settings.Save(check); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException("The update setting could not be saved: " + ex.Message); }
        Changed?.Invoke();
    }

    /// <summary>Once per launch, after a short delay so starting stays light, when the host has not turned it off.</summary>
    public void StartLaunchCheck()
    {
        lock (sync) { if (launchChecked) return; launchChecked = true; }
        if (client == null || !CheckAtLaunch) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(options.LaunchDelay, stop.Token); await Check(); }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Looks for a newer release now. False when another update step is running.</summary>
    public async Task<bool> Check()
    {
        if (client == null || !await busy.WaitAsync(0)) return false;
        try
        {
            if (Status.State is "installing") return false;
            Forced = false;
            Set(new("checking", options.Current));
            var result = await client.Check(CurrentVersion, stop.Token);
            if (result.Release is { } found)
            {
                release = found;
                if (options.LocationProblem(options.ExePath!) is { } problem)
                {
                    Set(new("unsupported", options.Current, found.Version, found.NotesUrl, Reason: problem,
                        Error: problem == UpdateReasons.NotNtfs ? "Soundpad Web can update itself only from a folder on a local NTFS drive." : "Soundpad Web cannot write to its own folder."));
                    Log?.Invoke($"Version {found.Version} is available; this folder cannot be updated in place ({problem}).");
                }
                else
                {
                    Set(new("available", options.Current, found.Version, found.NotesUrl, Total: found.ExeSize));
                    Log?.Invoke($"Version {found.Version} is available.");
                }
            }
            else if (result.Reason != null) { Set(new("failed", options.Current, Reason: result.Reason, Error: result.Error, NotesUrl: options.Source!.ReleasesPage)); Log?.Invoke("The update check failed: " + result.Error); }
            else Set(new("idle", options.Current));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { busy.Release(); }
    }

    /// <summary>Downloads and verifies <paramref name="version"/>. True when it is ready to install.</summary>
    public async Task<bool> Download(string version)
    {
        if (Forced) return Status.State == "ready" || Status.State == "available";
        if (client == null || release is not { } found || found.Version != version) return false;
        if (!await busy.WaitAsync(0)) return false;
        string? path = null;
        try
        {
            if (Status.State == "ready" && DownloadPath != null && File.Exists(DownloadPath)) return true;
            var exe = options.ExePath!;
            if (options.LocationProblem(exe) is { } problem) { Set(new("unsupported", options.Current, found.Version, found.NotesUrl, Reason: problem, Error: "Soundpad Web cannot update itself in this folder.")); return false; }
            // 2.48: a versioned EXE is updated to the new version's name; a file already there is the host's, never replaced.
            var target = UpdateNames.Target(exe, found.Version);
            if (!string.Equals(target, exe, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                throw new UpdateRefusedException(UpdateReasons.NameTaken, $"{Path.GetFileName(target)} is already in this folder. Start that one, or move it away and update again.");
            path = Path.Combine(Path.GetDirectoryName(exe)!, $".{Path.GetFileNameWithoutExtension(exe)}.{found.Version}.download");
            Set(new("downloading", options.Current, found.Version, found.NotesUrl, 0, found.ExeSize));
            using var sha = new MemoryStream();
            await client.Download(found.ShaUrl, sha, ReleaseClient.MaxShaBytes, null, stop.Token);
            long bytes; long? length;
            long lastReport = 0;
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                (bytes, length) = await client.Download(found.ExeUrl, file, ReleaseClient.MaxExeBytes, (done, total) =>
                {
                    long now = Environment.TickCount64;
                    if (now - lastReport < 250) return;
                    lastReport = now;
                    Set(new("downloading", options.Current, found.Version, found.NotesUrl, done, found.ExeSize ?? total));
                }, stop.Token);
                await file.FlushAsync(stop.Token); file.Flush(true);
            }
            Set(new("verifying", options.Current, found.Version, found.NotesUrl, bytes, found.ExeSize ?? length));
            var shaText = System.Text.Encoding.UTF8.GetString(sha.ToArray());
            await Task.Run(() => UpdateVerifier.Verify(path, found, bytes, length, shaText), stop.Token);
            var (exit, output) = await options.Probe(path, stop.Token);
            if (exit != 0 || !output.Contains(UpdateVerifier.NoticesMarker, StringComparison.Ordinal))
                throw new UpdateRefusedException(UpdateReasons.Blocked, "The new version would not start on this PC (Windows or an antivirus blocked it).");
            DownloadPath = path; verifiedHash = Sha256Text.ParseFor(shaText, found.ExeName);
            Set(new("ready", options.Current, found.Version, found.NotesUrl, bytes, bytes));
            Log?.Invoke($"Version {found.Version} is downloaded and checked against the hash published with the release.");
            return true;
        }
        catch (Exception ex) when (ex is UpdateRefusedException or HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (path != null) { try { File.Delete(path); } catch (Exception) { } }
            DownloadPath = null;
            if (stop.IsCancellationRequested) return false;
            var reason = ex is UpdateRefusedException refused ? refused.Reason : UpdateReasons.Network;
            var words = ex is UpdateRefusedException ? ex.Message : ex is OperationCanceledException ? "The download stalled." : "The download failed: " + ex.Message;
            Set(new("failed", options.Current, found.Version, found.NotesUrl, Reason: reason, Error: words));
            Log?.Invoke("The update was not installed: " + words);
            return false;
        }
        finally { busy.Release(); }
    }

    /// <summary>
    /// Right before the swap: the downloaded EXE still has the hash published with the release (it may have waited
    /// since it was checked). A forced (test) state has no file and passes. False removes the download.
    /// </summary>
    public bool StillVerified()
    {
        if (Forced) return true;
        var path = DownloadPath; var hash = verifiedHash;
        try { if (path != null && hash != null && File.Exists(path) && UpdateVerifier.Hash(path) == hash) return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (path != null) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        DownloadPath = null;
        return false;
    }

    /// <summary>The swap is starting (Bridge): pages show "installing". <paramref name="note"/> says how the backup went.</summary>
    public void Installing(string? note)
    {
        var s = Status;
        Set(new("installing", options.Current, s.Latest, s.NotesUrl, s.Bytes, s.Total, note));
    }

    /// <summary>The install could not start (refused, or the download failed): back to the last offer, with why.</summary>
    public void Refused(string reason, string words)
    {
        var s = Status;
        Set(new("failed", options.Current, s.Latest, s.NotesUrl ?? options.Source?.ReleasesPage, Reason: reason, Error: words));
    }

    /// <summary>Harness verb update-state: shows a state without any network or file (installing then skips the files).</summary>
    public void Force(UpdateStatus next) { Forced = true; Set(next with { Current = options.Current }); }

    public void Dispose() { stop.Cancel(); client?.Dispose(); }
}
