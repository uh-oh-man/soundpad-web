namespace SoundpadWeb;

/// <summary>A person's choice at the quit prompt (2.40): keep these clips first, quit anyway, or cancel.</summary>
public sealed record QuitAnswer(string Action, string[] AssetIds);

/// <summary>
/// What closing the companion window does while Temp holds clips (2.40). The window's page asks when it can; when no
/// page can (End hosting closed its connection, an older website, a prompt left unanswered), the window asks itself,
/// so quitting never removes Temp clips without asking.
/// </summary>
public static class QuitQuestion
{
    /// <param name="askPage">Asks the window's page (<see cref="LocalServer.AskQuit"/>): false when there is none to ask.</param>
    /// <param name="askHere">Asks in the window itself: "keep", "quit" or "cancel".</param>
    /// <param name="clipIds">The clips in Temp, for "keep" asked in the window (it keeps them all).</param>
    /// <param name="answered">Runs once with the person's answer, unless it was "quit" asked in the window.</param>
    /// <returns>True when the window stays open for now (asked the page, cancelled, or keeping the clips before quitting).</returns>
    public static bool Ask(int clips, Func<int, Action<QuitAnswer>, bool> askPage, Func<int, string> askHere, Func<string[]> clipIds, Action<QuitAnswer> answered)
    {
        if (clips <= 0) return false;
        if (askPage(clips, answered)) return true;
        switch (askHere(clips))
        {
            case "quit": return false;
            case "keep": answered(new("keep", clipIds())); return true;
            default: return true;
        }
    }
}

// Guest Keep and storage budgets, Temp lifecycle and crash recovery (2.40). See reports/OVERHAUL-PLAN.md, "Data
// lifecycle, updates and setup" and the budget bullets in "Playback and connection are first priorities".
public sealed partial class Bridge
{
    private KeepSettings? keepSettings;
    private readonly object keepGate = new();
    private readonly GuestBudgets budgets = new(Path.Combine(store.DirectoryPath, "guest-kept.json"));
    /// <summary>Temp clips a launch that did not close normally left, until the host recovers or discards them.</summary>
    private volatile int tempRecovery;
    /// <summary>
    /// Exactly the temporary files that launch left (2.40), so Discard removes those and nothing made since: hosting
    /// goes on in the same journal session meanwhile, and its new clips land in the same Temp category.
    /// </summary>
    private volatile TemporaryFiles[] tempLeftovers = [];
    private string KeepSettingsPath => Path.Combine(store.DirectoryPath, "keep-settings.json");
    private KeepSettings Keep { get { lock (keepGate) return keepSettings ??= KeepSettings.Load(KeepSettingsPath); } }
    /// <summary>Test seam and harness: guest budgets as they stand.</summary>
    public GuestBudgets Budgets => budgets;

    private Permissions PublicPermissions => disk.Permissions with { GuestKeep = Keep.GuestKeep };

    private void SaveKeep(KeepSettings next)
    {
        lock (keepGate) keepSettings = next;
        try { next.Save(KeepSettingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Keep settings could not be saved: " + ex.Message); }
        budgets.Touch();
    }

    /// <summary>The ids of the assets that are in Temp right now (a lookup for the budget sums).</summary>
    private Func<string, bool> TemporaryIds()
    {
        var ids = Audio.Public.Assets.Where(a => a.HasTemporary).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return ids.Contains;
    }

    /// <summary>Clips in Temp that quitting would remove: sources with a current temporary file not already on its way out.</summary>
    public int TemporaryClipCount
    {
        get
        {
            try { return Audio.Public.Assets.Count(IsTemporaryClip); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; }
        }
    }
    /// <summary>The clips <see cref="TemporaryClipCount"/> counts (Keep at the companion window's own quit question).</summary>
    public string[] TemporaryClipIds()
    {
        try { return [.. Audio.Public.Assets.Where(IsTemporaryClip).Select(a => a.Id)]; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
    }
    private static bool IsTemporaryClip(AudioAssetView a) => a.Variants.Any(v => v.Current && !v.Permanent && !v.LegacyApproval && v.NativeLabel != null && v.State != "cleanup-pending");

    /// <summary>The temporary variant Keep takes for a clip: its normal one, else its reversed one.</summary>
    private AudioVariant? KeepableVariant(string assetId)
    {
        var asset = Audio.AssetById(assetId);
        bool Fits(AudioVariant v) => v.Current && !v.Permanent && v.OwnsFile && !v.Retired && v.State == "ready";
        return asset.Variants.LastOrDefault(v => v.Orientation == "normal" && Fits(v)) ?? asset.Variants.LastOrDefault(Fits);
    }

    /// <summary>
    /// Startup (2.40). After an ordinary quit the session is closed and its Temp is cleaned up as before. A launch that
    /// did not close normally (a crash, power loss) left its session open with its clips in Temp: they stay until the
    /// host chooses Recover (keep using them) or Discard (the cleanup an ordinary quit would have done).
    /// </summary>
    public async Task StartupCleanup()
    {
        int left = 0;
        try { if (!Audio.Public.Blocked && Audio.SessionOpen) left = TemporaryClipCount; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { left = 0; }
        if (left > 0)
        {
            tempLeftovers = Audio.CurrentTemporaryFiles();
            tempRecovery = left; recoveryPending = false;
            Log($"The companion did not close normally last time: {left} clip(s) from that session are still in Temp. Recover or discard them.");
            Publish();
            return;
        }
        await CleanupSession();
    }

    /// <summary>True when every temporary file there is now is one the last launch left (nothing was made since).</summary>
    private bool OnlyLeftoversInTemp()
    {
        var left = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in tempLeftovers) { foreach (var v in f.VariantIds) left.Add(f.AssetId + "/" + v); if (f.InputPath != null) left.Add(f.AssetId + "|" + f.InputPath); }
        return Audio.CurrentTemporaryFiles().All(f => f.VariantIds.All(v => left.Contains(f.AssetId + "/" + v)) && (f.InputPath == null || left.Contains(f.AssetId + "|" + f.InputPath)));
    }

    /// <summary>Quit prompt "Keep selected" (2.40): these clips are kept permanently, in the host's Keep destination, before quitting.</summary>
    public async Task<int> KeepForQuit(IEnumerable<string> assetIds)
    {
        int kept = 0;
        await audioGate.EnterAsync(PriorityGate.Lane.Normal);
        try
        {
            var settings = Keep;
            foreach (var id in assetIds.Distinct().Take(256))
            {
                try
                {
                    if (KeepableVariant(id) is not { } variant) continue;
                    await Audio.Promote(id, variant.Id, settings.Destination, settings.CategoryId, settings.CategoryName, true);
                    kept++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Log($"Could not keep a clip before quitting: {ex.Message}"); }
            }
        }
        finally { audioGate.Release(); }
        if (kept > 0) Log($"Kept {kept} clip(s) before quitting");
        return kept;
    }

    /// <summary>The 2.40 keep, budget and recovery commands. Null when the op is not one of them.</summary>
    private async Task<Reply?> ExecuteKeep(Command c, string actor, Participant? participant, string? requester, string? audience)
    {
        if (c.Op is not ("keepSettings" or "budgetStatus" or "budgetGrant" or "tempRecover")) return null;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host && c.Op != "budgetStatus") throw new InvalidDataException("Host permission required.");
            if (!host && (audioAudience != null && (!sessionAccepting || audience != audioAudience) || requester != null && revokedAudioPeers.ContainsKey(requester))) throw new InvalidDataException("This guest session authorization changed.");
            var settings = Keep;
            var temporary = TemporaryIds();
            switch (c.Op)
            {
                case "budgetStatus":
                    if (!host)
                    {
                        var own = budgets.Usage(PersonKey(actor, participant, requester), settings, temporary);
                        return new("ack", c.Id, true, Data: new { guestKeep = settings.GuestKeep, own, budgetMode = settings.BudgetMode });
                    }
                    return new("ack", c.Id, true, Data: new { settings, guests = budgets.All(settings, temporary), requests = budgets.Requests() });
                case "keepSettings":
                    {
                        string? destination = settings.Destination;
                        if (c.DestinationDirectory != null)
                        {
                            var d = c.DestinationDirectory.Trim();
                            if (d.Length == 0) destination = null;
                            else if (d.Length > 1024 || !Path.IsPathFullyQualified(d) || d.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("Choose a local folder such as D:\\Sounds for kept clips.");
                            else destination = d;
                        }
                        int? categoryId = settings.CategoryId; string? categoryName = settings.CategoryName;
                        if (c.CategoryId != null || c.CategoryName != null)
                        {
                            if (c.CategoryId is int id && id >= 0 && c.CategoryName is { Length: > 0 and <= 256 } name) { categoryId = id; categoryName = name; }
                            else { categoryId = null; categoryName = null; }
                        }
                        if (c.TempMb is int temp && temp is < 1 or > KeepSettings.MaxTempMb) throw new InvalidDataException($"Give each guest 1 to {KeepSettings.MaxTempMb} MB of Temp.");
                        if (c.KeptMb is int kept && kept is < 1 or > KeepSettings.MaxKeptMb) throw new InvalidDataException($"Give each guest 1 to {KeepSettings.MaxKeptMb} MB of kept audio.");
                        if (c.BudgetMode != null && !KeepSettings.ValidMode(c.BudgetMode)) throw new InvalidDataException("Count kept audio per session or in total.");
                        var next = settings with { Destination = destination, CategoryId = categoryId, CategoryName = categoryName, TempMb = c.TempMb ?? settings.TempMb, KeptMb = c.KeptMb ?? settings.KeptMb, BudgetMode = c.BudgetMode ?? settings.BudgetMode, GuestKeep = c.GuestKeep ?? settings.GuestKeep };
                        SaveKeep(next);
                        Publish();
                        return new("ack", c.Id, true, Data: new { settings = next });
                    }
                case "budgetGrant":
                    {
                        if (c.GuestId is not { Length: > 0 and <= 128 } guest) throw new InvalidDataException("Choose a guest.");
                        bool done = c.Action == "allow" ? budgets.Grant(guest, settings) : c.Action == "deny" ? budgets.Deny(guest) : throw new InvalidDataException("Allow or deny the request.");
                        if (done) Log(c.Action == "allow" ? "Allowed a guest more storage until the companion closes" : "Left a guest's storage request for now");
                        Publish();
                        return new("ack", c.Id, true, Data: new { done });
                    }
                case "tempRecover":
                    {
                        if (c.Action is not ("recover" or "discard")) throw new InvalidDataException("Recover or discard the previous session's Temp.");
                        await audioGate.EnterAsync(PriorityGate.Lane.Normal);
                        try
                        {
                            if (c.Action == "recover") { if (!Audio.Public.Blocked) await Audio.Recover(); }
                            else if (!Audio.Public.Blocked)
                            {
                                // Only what the last launch left: clips made since (hosting carries on in the same
                                // journal session and Temp category) stay, as does the session itself.
                                await StopTemporaryPlayback();
                                if (OnlyLeftoversInTemp())
                                {
                                    // Nothing new: the cleanup an ordinary quit would have done, Temp category and all.
                                    Audio.RetireSession(); await Audio.Cleanup();
                                    if (sessionAccepting) Audio.BeginSession();
                                }
                                else foreach (var files in tempLeftovers)
                                {
                                    try { await Audio.RetireFiles(files); }
                                    catch (InvalidDataException ex) { Log("A clip from the last session could not be discarded: " + ex.Message); }
                                }
                            }
                        }
                        finally { audioGate.Release(); }
                        int was = tempRecovery; tempRecovery = 0; tempLeftovers = [];
                        Log(c.Action == "recover" ? $"Recovered {was} clip(s) in Temp from the last session" : "Discarded the last session's Temp");
                        await gate.EnterAsync(PriorityGate.Lane.Normal); try { await RefreshInner(); Publish(); } finally { gate.Release(); }
                        return new("ack", c.Id, true);
                    }
            }
            return null;
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
    }

    /// <summary>A guest's Keep (2.40): into the host's destination, within their permanent budget. The caller holds the audio gate.</summary>
    private async Task GuestKeep(Command c, string actor, Participant? participant, string? requester)
    {
        var settings = Keep;
        var assetId = c.AssetId ?? throw new InvalidDataException("Choose a clip to keep.");
        var asset = Audio.AssetById(assetId);
        var variant = (c.VariantId != null ? asset.Variants.SingleOrDefault(v => v.Id == c.VariantId) : KeepableVariant(assetId)) ?? throw new InvalidDataException("That clip is not ready to keep yet.");
        if (variant.Permanent) return;
        string guest = PersonKey(actor, participant, requester), name = Person(actor, participant).Name;
        if (budgets.CheckKeep(guest, name, variant.Bytes, settings) is { } refusal) { Publish(); throw new InvalidDataException(refusal); }
        // The host chose the destination once; a guest's command never carries a path.
        await Audio.Promote(assetId, variant.Id, settings.Destination, settings.CategoryId, settings.CategoryName, true);
        budgets.NoteKept(guest, name, variant.Bytes);
        Log($"{name} kept \"{asset.Name}\"");
    }
}
