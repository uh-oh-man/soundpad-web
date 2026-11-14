namespace SoundpadWeb;

/// <summary>A person's choice at the quit prompt (2.40): keep these clips first, quit anyway, or cancel.</summary>
public sealed record QuitAnswer(string Action, string[] AssetIds);

// Guest Keep and storage budgets, Temp lifecycle and crash recovery (2.40). See reports/OVERHAUL-PLAN.md, "Data
// lifecycle, updates and setup" and the budget bullets in "Playback and connection are first priorities".
public sealed partial class Bridge
{
    private KeepSettings? keepSettings;
    private readonly object keepGate = new();
    private readonly GuestBudgets budgets = new(Path.Combine(store.DirectoryPath, "guest-kept.json"));
    /// <summary>Temp clips a launch that did not close normally left, until the host recovers or discards them.</summary>
    private volatile int tempRecovery;
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
            tempRecovery = left; recoveryPending = false;
            Log($"The companion did not close normally last time: {left} clip(s) from that session are still in Temp. Recover or discard them.");
            Publish();
            return;
        }
        await CleanupSession();
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
                                await StopTemporaryPlayback();
                                Audio.RetireSession(); await Audio.Cleanup();
                                if (sessionAccepting) Audio.BeginSession();
                            }
                        }
                        finally { audioGate.Release(); }
                        int was = tempRecovery; tempRecovery = 0;
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
