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
                journal.SavedCategory ??= new() { Name = "Saved · Soundpad Web [" + journal.AppId[..6] + "-" + Guid.NewGuid().ToString("N")[..6] + "]" }; Save();
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
                catch (Exception ex) when (ex is not OutOfMemoryException) { Issue(asset, "native-import", "A prepared file is waiting for a verified native entry. Open Soundpad or resolve native duplicates, then retry."); }
            }
        }
        Save(); await Cleanup();
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
    public async Task Cleanup(string? assetId = null)
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
        Save();
    }
    public object[] HostLocations() => Volatile.Read(ref hostLocations);
}
