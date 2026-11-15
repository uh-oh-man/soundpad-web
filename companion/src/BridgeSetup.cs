using System.Text.Json;
namespace SoundpadWeb;

/// <summary>
/// The guided setup, Soundpad's diagnosis and the host-wide hotkeys (2.44, gates setup and hostHotkeys). Every op is
/// the host's: guests and relayed commands are refused here, not only in the page. See docs/SETUP.md.
/// </summary>
public sealed partial class Bridge
{
    // ---- Soundpad diagnosis ----
    private SoundpadDiagnosis diagnosis = new();
    private volatile string? soundpadProblem;
    /// <summary>Tests and the harness: whether Soundpad runs (read-only by default: a process list).</summary>
    public void UseSoundpadRunning(Func<bool> running) => diagnosis = new(running);
    /// <summary>Called by RefreshInner and Poll when a Soundpad request failed.</summary>
    private void NoteSoundpadFailure(Exception ex) => soundpadProblem = diagnosis.Classify(ex);
    /// <summary>Why Soundpad can't be reached; null while it answers (a recovery clears it with no extra step).</summary>
    private string? SoundpadProblemNow => available ? null : soundpadProblem;
    private readonly Lazy<bool> elevated = new(SoundpadDiagnosis.Elevated);
    private bool? CompanionElevatedNow => elevated.Value;

    // ---- Setup ----
    private SetupStore? setupStore;
    private SetupStore Setup => setupStore ??= new(Path.Combine(store.DirectoryPath, "setup.json"));
    private bool? existingInstall;
    /// <summary>Kept data from before this launch: decided once, at startup, before this launch adds any.</summary>
    private bool ExistingInstall => existingInstall ??= DetectExisting();
    private bool DetectExisting()
    {
        bool permanent = false;
        try { permanent = Audio.Public.Assets.Any(a => a.Variants.Any(v => v.Permanent)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return SetupStore.Existing(store.DirectoryPath, disk, permanent);
    }
    private bool? SetupDoneNow { get { try { return Setup.Read().Done; } catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } } }

    // ---- Host-wide hotkeys ----
    private Func<HotkeyChord, IGlobalHotkey> makeHostHotkey = HostHotkeys.WindowsFactory;
    private bool? noHotkeys;
    private HostHotkeys? hostHotkeys;
    private readonly object hostHotkeysGate = new();
    private HostHotkeys HostKeys
    {
        get
        {
            lock (hostHotkeysGate)
                return hostHotkeys ??= new(Path.Combine(store.DirectoryPath, "hotkeys.json"), chord => makeHostHotkey(chord), action => _ = HostHotkeyPressed(action),
                    noHotkeys ?? Environment.GetCommandLineArgs().Contains("--no-hotkeys"));
        }
    }
    /// <summary>Replaces how host-wide hotkeys are registered; tests and the harness use fixtures (never Windows).</summary>
    public void UseHostHotkeys(Func<HotkeyChord, IGlobalHotkey> factory) => makeHostHotkey = factory;
    /// <summary>Tests: as if the companion was started with --no-hotkeys.</summary>
    internal void UseNoHotkeys(bool off) => noHotkeys = off;
    private long HotkeysRevision => hostHotkeys?.Revision ?? 0;
    private readonly Dictionary<string, long> lastHostPress = [];
    /// <summary>Tests: the last press's work, so a test can wait for it.</summary>
    internal Task LastHostPress { get; private set; } = Task.CompletedTask;

    /// <summary>After startup cleanup (and an update resume): the stored host-wide hotkeys are registered.</summary>
    private void SetupStarted()
    {
        _ = ExistingInstall;
        var keys = HostKeys;
        keys.RegisterAll(hotkey.Name);
        if (keys.Damage is { } damage) Log(damage);
        foreach (var (action, status) in keys.Statuses)
        {
            var chord = keys.Bindings[action];
            if (chord == null) continue;
            Log(status.State == "on" ? $"Host-wide {HostHotkeys.Labels[action]} key: {chord}" : $"Host-wide {HostHotkeys.Labels[action]} key {chord} is off: {status.Message}");
        }
        Publish();
    }
    private void SetupDispose() { lock (hostHotkeysGate) hostHotkeys?.Dispose(); }
    /// <summary>Tests and the harness: the chord bound to <paramref name="action"/>, if any.</summary>
    internal string? HostHotkeyBinding(string action) => HostKeys.Bindings.GetValueOrDefault(action);

    /// <summary>
    /// A host-wide hotkey was pressed (on its own thread): the work runs on the thread pool, never holding the gate on
    /// the hotkey's thread. Presses within 300 ms of the last one of the same action are ignored.
    /// </summary>
    internal Task HostHotkeyPressed(string action)
    {
        lock (lastHostPress)
        {
            long now = Environment.TickCount64;
            if (lastHostPress.TryGetValue(action, out long last) && now - last < 300) return Task.CompletedTask;
            lastHostPress[action] = now;
        }
        var work = Task.Run(() => RunHostHotkey(action));
        LastHostPress = work;
        return work;
    }

    private static Command HostCommand(string op) => new(2, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);

    private async Task RunHostHotkey(string action)
    {
        string label = HostHotkeys.Labels[action], note;
        try
        {
            Reply reply;
            switch (action)
            {
                case "capture":
                    if (mimicCapture.Running) { reply = await Execute(HostCommand("mimicStop"), "host"); note = reply.Ok ? "Stopped listening" : reply.Error ?? "Could not stop listening."; break; }
                    var source = RememberedSource(mimicCapture.Sources());
                    if (source == null) { reply = new("ack", "", false, "Choose an output in Setup or Mimic first."); note = reply.Error!; break; }
                    reply = await Execute(HostCommand("mimicStart") with { SourceId = source.Id }, "host");
                    note = reply.Ok ? $"Listening to {source.Name}" : reply.Error ?? "Could not start listening.";
                    break;
                case "panic":
                    reply = await Execute(HostCommand("panic") with { Action = "enable" }, "host");
                    note = reply.Ok ? "Panic: stopped, guest playback paused" : reply.Error ?? "Panic failed.";
                    break;
                case "stop":
                    reply = await Execute(HostCommand("stop"), "host");
                    note = reply.Ok ? "Stopped playback" : reply.Error ?? "Stop failed.";
                    break;
                default: return;
            }
            Log($"Host-wide {label} key: {note}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { note = "Could not run: " + ex.Message; Log($"Host-wide {label} key: {note}"); }
        HostKeys.NotePress(action, $"{note} at {DateTime.Now:HH:mm:ss}");
        Publish(); CommandAccepted?.Invoke();
    }

    /// <summary>
    /// The source chosen last time (mimicStart or Setup's "Remember this output"), if the current list offers it: by
    /// id, or for an app by its name without the process id. Only an output or an app; never an input.
    /// </summary>
    private MimicSource? RememberedSource(IReadOnlyList<MimicSource> sources) =>
        sources.FirstOrDefault(s => s.Id == Settings.SourceId && s.Kind is "output" or "app")
        ?? (Settings.SourceName is { } named ? sources.FirstOrDefault(s => s.Kind == "app" && StableSourceName(s.Name) == StableSourceName(named)) : null);

    // ---- Ops ----
    private static bool IsSetupOp(string op) => op is "setupGet" or "setupSet" or "hotkeysGet" or "hotkeysSet";

    private Task<Reply>? ExecuteSetup(Command c, string actor, Participant? p, string? requester, string? audience)
    {
        if (!IsSetupOp(c.Op)) return null;
        // Listing outputs and registering keys may take a moment: never on the caller's thread.
        return Task.Run(() => SetupReply(c, actor, p));
    }

    private Reply SetupReply(Command c, string actor, Participant? p)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            if (actor != "host") throw new InvalidDataException("Host permission required.");
            switch (c.Op)
            {
                case "setupGet": return new("ack", c.Id, true, Data: SetupData());
                case "setupSet":
                    {
                        var body = Body(c, ["action"]);
                        var action = body.TryGetValue("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                        if (action is not ("done" or "reset" or "dismiss")) throw new InvalidDataException("Unsupported setup action.");
                        var record = Setup.Set(action, p?.Name is { Length: > 0 } name ? name : "Host", now);
                        Log(action switch { "done" => "Setup finished", "dismiss" => "Setup check dismissed", _ => "Setup will show again" });
                        Publish();
                        return new("ack", c.Id, true, Data: new { done = record.CompletedAt != null, dismissed = record.Dismissed });
                    }
                case "hotkeysGet": return new("ack", c.Id, true, Data: HotkeysData());
                case "hotkeysSet":
                    {
                        var body = Body(c, HostHotkeys.Actions);
                        var changes = new Dictionary<string, string?>();
                        foreach (var (name, value) in body)
                        {
                            if (value.ValueKind == JsonValueKind.Null) changes[name] = null;
                            else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= HotkeyChord.MaxLength } text) changes[name] = text;
                            else throw new InvalidDataException("Send a key combination such as Ctrl+Alt+F9, or null to turn the key off.");
                        }
                        if (changes.Count == 0) throw new InvalidDataException("Send the hotkeys to change.");
                        HostKeys.Apply(changes, hotkey.Name);
                        foreach (var (action, status) in HostKeys.Statuses.Where(s => changes.ContainsKey(s.Key)))
                            Log(status.State == "on" ? $"Host-wide {HostHotkeys.Labels[action]} key is now {HostKeys.Bindings[action]}" : status.State == "off" ? $"Host-wide {HostHotkeys.Labels[action]} key off" : $"Host-wide {HostHotkeys.Labels[action]} key: {status.Message}");
                        Publish();
                        return new("ack", c.Id, true, Data: HotkeysData());
                    }
                default: throw new InvalidDataException("Unsupported command.");
            }
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("ack", c.Id, false, "The setting could not be saved. Check the companion's data folder."); }
    }

    /// <summary>The op's body: an object with only <paramref name="allowed"/> fields (each at most once), or none.</summary>
    private static Dictionary<string, JsonElement> Body(Command c, IReadOnlyCollection<string> allowed)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (c.Body is not { } body || body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return fields;
        if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid request.");
        foreach (var field in body.EnumerateObject())
            if (!allowed.Contains(field.Name) || !fields.TryAdd(field.Name, field.Value)) throw new InvalidDataException("Invalid request.");
        return fields;
    }

    private object SetupData()
    {
        var record = Setup.Read();
        IReadOnlyList<MimicSource> sources = [];
        try { sources = mimicCapture.Sources(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* no audio devices to list: the step says so */ }
        var outputs = sources.Where(s => s.Kind == "output").Select(s => new { id = s.Id, name = s.Name }).ToArray();
        var remembered = RememberedSource(sources) is { Kind: "output" } output ? output.Id : null;
        return new
        {
            done = record.CompletedAt != null,
            dismissed = record.Dismissed,
            existing = ExistingInstall,
            soundpad = new { available, problem = SoundpadProblemNow, error, elevated = elevated.Value },
            outputs,
            remembered,
        };
    }

    private object HotkeysData() => new
    {
        bindings = HostKeys.Bindings,
        status = HostKeys.Statuses.ToDictionary(s => s.Key, s => new { state = s.Value.State, message = s.Value.Message, last = s.Value.Last }),
        mimicKey = hotkey.Name,
        disabled = HostKeys.Disabled,
        problem = HostKeys.Damage,
    };

    /// <summary>
    /// mimicSources "remember" (2.44): saves an output as the source Capture starts on, without starting capture or
    /// changing any device or routing. Only an output the current list offers, and only the host's.
    /// </summary>
    private Reply RememberOutput(Command c)
    {
        var source = mimicCapture.Sources().FirstOrDefault(s => s.Id == c.SourceId && s.Kind == "output" && s.Id.Length <= 512)
            ?? throw new InvalidDataException("Choose an output from the list (what this PC plays). Refresh the list if it changed.");
        SaveSettings(Settings with { SourceId = source.Id, SourceName = source.Name });
        Log($"Remembered {source.Name} for Capture (nothing is recorded until you start listening)");
        return new("ack", c.Id, true, Data: new { remembered = source.Id });
    }
}
