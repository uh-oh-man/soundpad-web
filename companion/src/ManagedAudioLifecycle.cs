namespace SoundpadWeb;

public sealed partial class ManagedAudioStore
{
    public async Task<AudioAsset> Promote(string assetId, string variantId, string? destinationDirectory, int? categoryId, string? categoryName, bool confirmed)
    {
        RequireWritable(); if (!confirmed) throw new InvalidDataException("Confirm the permanent destination before saving.");
        var asset = Asset(assetId); var source = asset.Variants.SingleOrDefault(v => v.Id == variantId) ?? throw new InvalidDataException("Unknown variant.");
        if (!source.OwnsFile) throw new InvalidDataException("This original already belongs to the native library.");
        if (source.Permanent) { await EnsureImported(asset, source); return asset; }
        if (asset.Promotion is { State: "committed" } completed && completed.SourceVariantId == variantId)
        {
            var accepted = asset.Variants.Single(v => v.Id == completed.TargetVariantId);
            if (!ManagedFiles.Verified(accepted.Path, accepted.FileHash, accepted.Bytes)) throw new InvalidDataException("The accepted file is missing or changed. The source recovery record was preserved.");
            await EnsureImported(asset, accepted); return asset;
        }
        if (source.State != "ready" || !ManagedFiles.Verified(source.Path, source.FileHash, source.Bytes)) throw new InvalidDataException("A verified prepared source is required before promotion. It has not been deleted.");
        if (asset.Promotion is not { State: not "committed" })
        {
            var baseDirectory = string.IsNullOrWhiteSpace(destinationDirectory) ? Library : Path.Combine(destinationDirectory.Trim(), "Soundpad Web");
            if (!Path.IsPathFullyQualified(baseDirectory) || baseDirectory.StartsWith(@"\\") || ManagedFiles.Beneath(baseDirectory, Root) || ManagedFiles.Beneath(baseDirectory, LegacyRoot) || string.Equals(Path.GetFullPath(baseDirectory), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(baseDirectory), Path.GetFullPath(LegacyRoot), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a writable local permanent folder outside temporary audio storage.");
            var target = new AudioVariant { Orientation = source.Orientation, SourceHash = source.SourceHash, FileHash = source.FileHash, Bytes = source.Bytes, OwnsFile = true, Storage = "permanent", Current = false, State = "copying", WorkspaceKey = source.WorkspaceKey };
            target.Path = Path.Combine(baseDirectory, asset.Id, ManagedFiles.Name(asset.Name) + "-" + target.Orientation + "-" + target.Id[..8] + ".wav");
            asset.Promotion = new() { SourceVariantId = source.Id, TargetVariantId = target.Id, Destination = target.Path, CategoryId = categoryId, CategoryName = categoryName };
            asset.Variants.Add(target); asset.State = "promoting"; Save();
        }
        if (asset.Promotion!.SourceVariantId != source.Id) throw new InvalidDataException("Finish the pending promotion for this source before starting another.");
        try { await ResumePromotion(asset); await Cleanup(asset.Id); return asset; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            asset.State = "promotion-pending"; Issue(asset, "promotion", "Permanent saving did not finish. The temporary source and promotion plan are preserved. Check the destination folder and native category, then retry recovery; do not submit a second copy."); Save();
            throw new InvalidDataException(ex is InvalidDataException ? ex.Message : "Permanent saving failed. Check folder access and Soundpad, then retry the preserved promotion in Temp.");
        }
    }
    private async Task ResumePromotion(AudioAsset asset)
    {
        var plan = asset.Promotion ?? throw new InvalidDataException("Missing promotion plan.");
        if (plan.State == "committed") return;
        var source = asset.Variants.Single(v => v.Id == plan.SourceVariantId); var target = asset.Variants.Single(v => v.Id == plan.TargetVariantId);
        if (!ManagedFiles.Verified(source.Path, source.FileHash, source.Bytes)) throw new InvalidDataException("The temporary source changed or is missing. Promotion is paused; existing files were preserved.");
        ManagedFiles.CopyNew(source.Path, target.Path, source.FileHash, source.Bytes);
        target.FileHash = source.FileHash; target.Bytes = source.Bytes; plan.State = "copy-verified"; Save();
        await Native(async () =>
        {
            var catalog = await ManagedSoundpad.Read(pipe); int category; string name;
            if (plan.CategoryId is int selected)
            {
                var chosen = catalog.Categories.SingleOrDefault(c => c.Index == selected && c.Name == plan.CategoryName && c.Type != "1") ?? throw new InvalidDataException("The permanent category changed. Restore the selected destination or inspect the pending promotion in Temp.");
                if (journal.TempCategory?.NativeId == selected) throw new InvalidDataException("Choose a permanent category outside the app-owned Temp category.");
                category = chosen.Index; name = chosen.Name;
            }
            else
            {
                journal.SavedCategory ??= new() { Name = "Saved - Soundpad Web [" + journal.AppId[..6] + "-" + Guid.NewGuid().ToString("N")[..6] + "]" }; Save();
                category = await ManagedSoundpad.EnsureCategory(pipe, journal.SavedCategory, Save); name = journal.SavedCategory.Name;
            }
            plan.State = "import-pending"; Save();
            target.Native = await ManagedSoundpad.Import(pipe, target.Path, category, name); target.Native.SoundId = nativeId(target.Path);
            plan.CategoryId = category; plan.CategoryName = name; plan.State = "native-verified"; Save(); return true;
        });
        if (!ManagedFiles.Verified(target.Path, target.FileHash, target.Bytes) || target.Native is not { VerifiedAt: > 0 }) throw new InvalidDataException("The permanent file or its native entry could not be verified.");
        foreach (var previous in asset.Variants.Where(v => v.Orientation == target.Orientation && v.Current)) previous.Current = false;
        target.State = "ready"; target.Permanent = true; target.Current = true; target.Retired = false;
        source.Retired = true; source.Current = false; source.LegacyApproval = false;
        plan.State = "committed"; asset.State = "permanent"; asset.ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); ClearIssues(asset, "promotion", "legacy-approval"); Save();
    }
    public async Task Remove(string assetId)
    {
        RequireWritable(); var asset = Asset(assetId);
        var status = await Native(() => pipe.Request("GetPlayStatus()"));
        if (status != "STOPPED") throw new InvalidDataException("Stop playback before removing temporary audio. No playing file was removed.");
        if (asset.Promotion is { State: not "committed" }) throw new InvalidDataException("Resolve the pending permanent save first. Its source must remain available for recovery.");
        foreach (var variant in asset.Variants.Where(v => v.OwnsFile && !v.Permanent && !v.LegacyApproval && v.Storage is "session" or "legacy" && v.State != "removed")) variant.Retired = true;
        if (asset.Input != null) asset.Input.State = "retired";
        asset.State = "cleanup-pending"; Save(); await Cleanup(assetId);
    }
    public async Task Recover(bool retireSession = false)
    {
        RetryJournal(); if (retireSession) RetireSession();
        await CheckDuplicateCategories();
        foreach (var asset in journal.Assets)
        {
            if (asset.Promotion is { State: not "committed" })
            {
                try { await ResumePromotion(asset); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Issue(asset, "promotion", "A preserved permanent save still needs recovery. Check destination access and Soundpad, then retry."); Save(); }
            }
            foreach (var variant in asset.Variants.Where(v => v.Current && v.State == "ready" && (!v.Retired || v.Permanent || v.LegacyApproval) && (retireSession || v.Native is not { VerifiedAt: > 0 } || asset.State == "waiting-soundpad")))
            {
                try { await EnsureImported(asset, variant); ClearIssues(asset, "native-import"); asset.State = variant.LegacyApproval ? "accepted-needs-migration" : variant.Permanent ? "permanent" : "temporary"; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Report(asset, "native-import", "A prepared file is waiting for a verified native entry. Open Soundpad or resolve native duplicates, then retry.", ex); }
            }
        }
        Save(); await Cleanup();
    }
    /// <summary>
    /// Host action (2.18): clears all temporary audio now, mid-session, the same way End hosting does: retires
    /// every temporary file, removes the app's Temp category (and with it every Temp entry in Soundpad), deletes the
    /// files and forgets their records. Hosting continues; the next reverse or upload starts a fresh Temp category.
    /// Kept (permanent) audio is untouched. Returns how many temporary files were cleared.
    /// </summary>
    public async Task<int> ClearTemporary()
    {
        RequireWritable(); int count = 0;
        foreach (var asset in journal.Assets)
        {
            if (asset.Input is { State: not "retired" } input) input.State = "retired";
            foreach (var variant in asset.Variants.Where(v => v.OwnsFile && !v.Permanent && !v.LegacyApproval && v.State != "removed" && !v.Retired)) { variant.Retired = true; asset.State = "cleanup-pending"; count++; }
        }
        Save();
        await Cleanup(everything: true);
        return count;
    }
    /// <summary>Creates or finds the Temp category exactly as a reverse would, without importing or playing anything (harness check).</summary>
    internal Task<int> EnsureTempCategory()
    {
        journal.TempCategory ??= NewTempCategory();
        Save(); return Native(() => ManagedSoundpad.EnsureCategory(pipe, journal.TempCategory, Save));
    }
    private OwnedCategory NewTempCategory() => new() { Name = "Temp - Soundpad Web [" + journal.AppId[..6] + "-" + Guid.NewGuid().ToString("N")[..6] + "]" };
    /// <summary>How long Soundpad must have stayed stopped before the Temp category is removed (2.37).</summary>
    internal const int TempQuietMs = 1000;
    /// <summary>
    /// When the companion last played, stopped or saw something playing (<see cref="Environment.TickCount64"/>), so the
    /// Temp category is removed only a second after that (2.37). Without it, a second after Soundpad was seen stopped.
    /// </summary>
    public Func<long>? PlaybackActivity { get; set; }
    /// <summary>The app's exact-playback slot files (2.37): removable with the Temp category, by exact path.</summary>
    public Func<IReadOnlyCollection<string>>? OwnedSlotPaths { get; set; }
    /// <summary>
    /// Registers the exact-playback slot files (2.37) in the app's Temp category, creating the category the way an
    /// import does. Only while Soundpad is stopped: it looks again before each change and gives up (false) as soon as
    /// something plays, to be tried again later. A slot already listed in that category is kept as it is; one listed
    /// anywhere else makes it throw, as an import would, so nothing of the person's is ever claimed.
    /// </summary>
    internal async Task<bool> RegisterSlots(IReadOnlyList<string> paths)
    {
        RequireWritable();
        if (journal.TempCategory == null) { journal.TempCategory = NewTempCategory(); Save(); }
        var temp = journal.TempCategory;
        int? category = await Native(async () => await pipe.Request("GetPlayStatus()") == "STOPPED" ? await ManagedSoundpad.EnsureCategory(pipe, temp, Save) : (int?)null);
        if (category == null) return false;
        foreach (var path in paths)
        {
            bool added = await Native(async () =>
            {
                if (await pipe.Request("GetPlayStatus()") != "STOPPED") return false;
                await ManagedSoundpad.Import(pipe, path, category.Value, temp.Name); return true;
            });
            if (!added) return false;
        }
        return true;
    }
    /// <summary>Raises (or clears) the Temp issue offering to remove garbled duplicate categories, so the host sees it at once.</summary>
    private async Task CheckDuplicateCategories()
    {
        var owned = new[] { journal.TempCategory, journal.SavedCategory }.OfType<OwnedCategory>().ToArray();
        if (owned.Length == 0) return;
        try
        {
            var catalog = await Native(() => ManagedSoundpad.Read(pipe));
            int count = owned.Sum(r => ManagedSoundpad.GarbledCopies(catalog, r).Length);
            journal.Issues.RemoveAll(i => i.Code == "category-duplicates");
            if (count > 0) Issue(null, "category-duplicates", $"Soundpad has {count} cop{(count == 1 ? "y" : "ies")} of the Temp category with a garbled name, made by companions before 2.15.9. They stop reverse and uploads from finishing. Remove duplicate categories cleans up the empty ones.", "warning", "cleanup-categories");
            Save();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or System.Xml.XmlException) { /* Soundpad unavailable: checked again next time */ }
    }
    /// <summary>
    /// Host action: removes the empty garbled copies of this app's categories that companions before 2.15.9
    /// created in a loop. Only empty categories carrying this app's random tag are touched. Returns the count.
    /// </summary>
    public async Task<int> RemoveDuplicateCategories()
    {
        RequireWritable();
        var owned = new[] { journal.TempCategory, journal.SavedCategory }.OfType<OwnedCategory>().ToArray();
        int removed = owned.Length == 0 ? 0 : await Native(() => ManagedSoundpad.RemoveGarbledCopies(pipe, owned));
        journal.Issues.RemoveAll(i => i.Code == "category-duplicates"); Save();
        return removed;
    }
    public async Task RetryAsset(string assetId, CancellationToken cancel = default)
    {
        RequireWritable(); var asset = Asset(assetId);
        if (asset.Promotion is { State: not "committed" }) { await ResumePromotion(asset); return; }
        if (asset.Input is { State: "staged" } input && ManagedFiles.Verified(input.Path, input.Hash, input.Bytes))
        {
            Capacity(); var replacement = Allocate(asset, "normal");
            foreach (var previous in asset.Variants.Where(v => v.Orientation == "normal" && v.Current)) { previous.Current = false; previous.Retired = true; }
            asset.Variants.Add(replacement); asset.State = "processing"; Save();
            await Generate(asset, replacement, input.Path, false, cancel); asset.SourceHash = replacement.FileHash; replacement.SourceHash = asset.SourceHash; asset.SourcePath = replacement.Path; input.State = "retired"; ClearIssues(asset, "upload-processing"); Save(); await EnsureImported(asset, replacement); return;
        }
        foreach (var variant in asset.Variants.Where(v => v.Current && v.State == "ready" && !v.Retired)) await TryImport(asset, variant);
        await Cleanup(asset.Id); Save();
    }
    public async Task Cleanup(string? assetId = null, bool everything = false)
    {
        RequireWritable();
        string status;
        try { status = await Native(() => pipe.Request("GetPlayStatus()")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Issue(null, "native-unavailable", "Cleanup is waiting for Soundpad. All files and permanent copies are preserved.", "info"); Save(); return; }
        if (status != "STOPPED")
        {
            if (journal.Assets.Any(a => a.Variants.Any(v => v.Retired && v.State != "removed"))) { Issue(null, "playback-active", "Cleanup is paused while playback is active or its status is unknown. It will retry after playback stops.", "info"); Save(); }
            return;
        }
        journal.Issues.RemoveAll(i => i.Code is "native-unavailable" or "playback-active");
        if (assetId == null && (!journal.SessionOpen || everything)) await RemoveTempCategory();
        foreach (var asset in journal.Assets.Where(a => assetId == null || a.Id == assetId))
        {
            if (asset.Promotion is { State: not "committed" }) continue;
            foreach (var variant in asset.Variants.Where(v => v.Retired && v.OwnsFile && !v.Permanent && !v.LegacyApproval && v.Storage is "session" or "legacy" && v.State != "removed").ToArray())
            {
                try
                {
                    bool canDelete = await Native(async () =>
                    {
                        if (await pipe.Request("GetPlayStatus()") != "STOPPED") return false;
                        var catalog = await ManagedSoundpad.Read(pipe); var matches = catalog.Entries.Where(e => e.Path.Equals(variant.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (matches.Length > 0)
                        {
                            if (matches.Length != 1 || variant.Native?.SoundId != nativeId(variant.Path) || pipe is not IAtomicOwnedEntryRemoval { CanRemoveOwnedAtomically: true } atomic || !await atomic.RemoveOwnedEntry(variant.Path, catalog.Fingerprint)) return false;
                            catalog = await ManagedSoundpad.Read(pipe); if (catalog.Entries.Any(e => e.Path.Equals(variant.Path, StringComparison.OrdinalIgnoreCase))) return false;
                        }
                        return await pipe.Request("GetPlayStatus()") == "STOPPED";
                    });
                    if (!canDelete)
                    {
                        Issue(asset, "native-removal-required", "The native entry could not be removed with atomic ownership verification. In Soundpad, remove only the listed app-generated entry without deleting its file, then Retry cleanup. The app will not risk a selection-based deletion.", "warning", "manual-native-removal"); Save(); continue;
                    }
                    variant.State = "deleting-file"; Save();
                    ManagedFiles.DeleteVerified(variant.Path, variant.FileHash, variant.Bytes, variant.Storage == "legacy" ? LegacyRoot : Sessions);
                    variant.State = "removed"; variant.Current = false; Save();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Issue(asset, "cleanup-file", ex is InvalidDataException ? ex.Message : "A managed file is locked or cleanup could not finish. Its recovery record is preserved; retry when it is no longer in use."); Save();
                }
            }
            if (asset.Input is { State: "retired" } input)
            {
                try
                {
                    var catalog = await Native(() => ManagedSoundpad.Read(pipe));
                    if (!catalog.Entries.Any(e => e.Path.Equals(input.Path, StringComparison.OrdinalIgnoreCase))) { ManagedFiles.DeleteVerified(input.Path, input.Hash, input.Bytes, Incoming); asset.Input = null; Save(); }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Issue(asset, "staged-cleanup", "The staged upload could not be safely removed. It remains in recovery until it is unlocked or restored."); Save(); }
            }
            if (!asset.Variants.Any(v => v.Retired && v.State != "removed" && !v.Permanent)) ClearIssues(asset, "cleanup-file", "native-removal-required");
            if (asset.State is not ("waiting-soundpad" or "failed" or "accepted-needs-migration")) asset.State = asset.Variants.Any(v => v.Permanent && v.Current) ? "permanent" : asset.Variants.Any(v => v.OwnsFile && v.State != "removed") ? asset.Variants.Any(v => v.Retired && !v.Permanent) ? "cleanup-pending" : "temporary" : asset.Origin == "native" ? "native" : "removed";
        }
        // Forget records whose files are gone (2.16). They used to pile up: a sound reversed often hit the journal's
        // 64-variant limit for good, and cleaned-up uploads counted toward its 2,048-source limit.
        // Only at session end: during a session, a repeated Keep permanently or retry may still name an old record.
        if (assetId == null && (!journal.SessionOpen || everything))
        {
            foreach (var asset in journal.Assets) asset.Variants.RemoveAll(v => v.State == "removed" && !v.Current && !v.Permanent && !v.LegacyApproval);
            journal.Assets.RemoveAll(a => a.Origin == "upload" && a.Variants.Count == 0 && a.Input == null && a.Promotion == null);
            // Issues about a source with nothing left pending (all its temporary files gone) no longer apply (2.16.2).
            var pending = journal.Assets.Where(a => a.Input != null || a.Promotion is { State: not "committed" } || a.Variants.Any(v => v.OwnsFile && !v.Permanent && v.State != "removed")).Select(a => a.Id).ToHashSet();
            journal.Issues.RemoveAll(i => i.AssetId != null && !pending.Contains(i.AssetId));
        }
        Save();
    }
    /// <summary>
    /// At session end (or on the next start after the window was closed), removes the app's whole Temp category
    /// with one command, which takes every reversed or uploaded entry out of Soundpad at once (2.16). Only when
    /// every entry in it is one of this app's retired temporary files and nothing is playing; otherwise the
    /// per-entry cleanup below decides. The next session creates a fresh Temp category.
    /// </summary>
    private async Task RemoveTempCategory()
    {
        if (journal.TempCategory is not { State: "created", NativeId: int index } temp) return;
        try
        {
            // Soundpad 4.0.35 exited once when a category was removed right after one of its sounds played (2.37,
            // measured during the slot probe). So the category goes only when Soundpad was seen stopped, stayed
            // stopped for a second with nothing played by the companion meanwhile, and is still stopped.
            for (int round = 0; round < 3; round++)
            {
                long? stopped = await Native(async () => await pipe.Request("GetPlayStatus()") == "STOPPED" ? Environment.TickCount64 : (long?)null);
                if (stopped == null) return;
                long activity = PlaybackActivity?.Invoke() ?? long.MinValue;
                long wait = Math.Max(stopped.Value, activity) + TempQuietMs - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)Math.Min(wait, TempQuietMs));
                bool? done = await Native(async () =>
                {
                    if (await pipe.Request("GetPlayStatus()") != "STOPPED") return false;
                    if ((PlaybackActivity?.Invoke() ?? long.MinValue) != activity) return (bool?)null; // played meanwhile: wait again
                    var catalog = await ManagedSoundpad.Read(pipe);
                    var category = catalog.Categories.SingleOrDefault(c => c.Index == index && c.Name == temp.Name);
                    if (category == null)
                    {
                        // Already gone (removed by hand, or Soundpad was reset): start fresh next session.
                        if (!catalog.Categories.Any(c => c.Name == temp.Name)) { journal.TempCategory = null; Save(); }
                        return false;
                    }
                    var ours = journal.Assets.SelectMany(a => a.Variants).Where(v => v.Retired && v.OwnsFile && !v.Permanent && !v.LegacyApproval && v.State != "removed").Select(v => v.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    // The exact-playback slot files are the app's own too (2.37), by exact path; anything else still keeps the category.
                    foreach (var slot in OwnedSlotPaths?.Invoke() ?? []) if (Path.IsPathFullyQualified(slot) && SlotPool.IsSlotFile(slot)) ours.Add(slot);
                    if (category.Paths.Any(p => !ours.Contains(p))) return false;
                    await ManagedSoundpad.RemoveCategoryWithSounds(pipe, catalog, category);
                    journal.TempCategory = null; Save();
                    return true;
                });
                if (done != null) return;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException or System.Xml.XmlException)
        {
            Issue(null, "temp-category", ex is InvalidDataException ? ex.Message : "Soundpad was unavailable while removing the Temp category. Cleanup retries later.", "info"); Save();
        }
    }
    public object[] HostLocations() => Volatile.Read(ref hostLocations);
}
