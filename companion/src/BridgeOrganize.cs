using System.Security.Cryptography;
using System.Text;
namespace SoundpadWeb;

// Organize mode (2.44, gates organize and missingFiles): the host's organization plan, guests' proposals, Apply
// (OrganizeApply.cs) and the sounds whose files are missing. Paths stay here: the website gets ids, names and states.
public sealed partial class Bridge
{
    private OrganizeFiles? organizeFiles;
    private OrganizeFiles OrgFiles => organizeFiles ??= new(store.DirectoryPath);
    private readonly object organizeSync = new();
    private OrganizePlan? orgPlan;
    private bool orgLoaded, orgBlocked;
    private string? orgProblem;
    private long organizeRevision;
    private long OrganizeRevisionNow => Interlocked.Read(ref organizeRevision);
    private readonly Dictionary<string, Reply> organizeReplies = [];
    private readonly Queue<string> organizeReplyOrder = new();
    private string? leppsoftOverride;
    /// <summary>Tests: Soundpad's settings folder (where soundlist.spl is), instead of %APPDATA%\Leppsoft (Organize's checkpoint and the backup folder rules).</summary>
    internal void UseLeppsoftFolder(string folder) => leppsoftOverride = folder;
    /// <summary>Only read, never written: the checkpoint copies soundlist.spl from here. Tests and the harness use a fixture folder.</summary>
    private string LeppsoftFolder => leppsoftOverride ?? (pipe is SoundpadPipe { IsProduction: true } ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leppsoft") : Path.Combine(store.DirectoryPath, "vendor-fixture"));
    private Func<string, bool>? fileExists;
    /// <summary>
    /// Turns the missing-file scan on with this file check. The real companion always scans (File.Exists); test and
    /// harness Soundpads list made-up paths, so there it runs only when a test or --missing-fixture asks for it.
    /// </summary>
    internal void UseFileExists(Func<string, bool> exists) => fileExists = exists;
    private bool ScansFiles => fileExists != null || pipe is SoundpadPipe { IsProduction: true };
    private MissingFiles? missingFiles;
    private readonly object missingInitialization = new();
    private MissingFiles Missing
    {
        get
        {
            lock (missingInitialization)
            {
                if (missingFiles != null) return missingFiles;
                missingFiles = new(fileExists);
                missingFiles.Changed += () => Publish();
                return missingFiles;
            }
        }
    }
    internal Task MissingScanIdle() => Missing.Idle();
    /// <summary>Checks every library file again now (harness: a fixture file was deleted).</summary>
    internal void RescanMissingFiles() { var p = parsed; if (ScansFiles) Missing.Request(p.Public.Version, p.Paths, force: true); }
    /// <summary>The rollback fixture: the plan as a crash or conflict would leave it (paused, kept).</summary>
    internal void PauseOrganizePlanForFixture()
    {
        lock (organizeSync) { LoadPlan(); if (orgPlan == null) return; orgPlan.State = "paused"; orgPlan.Paused = "conflict"; orgPlan.PausedText = "Rollback fixture."; SavePlanLocked(); }
    }
    private Timer? missingTimer;
    private string? organizeSeenVersion;
    private (string Version, long Revision, Library Result)? missingView;
    private (string Fingerprint, NativeTree Tree)? cachedTree;

    private static bool IsOrganizeOp(string op) => op is "organizeGet" or "organizeEdit" or "organizeApply" or "organizeResolve" or "organizeDiscard" or "organizePropose" or "organizeWithdraw" or "organizeSettings";
    private Task<Reply>? ExecuteOrganize(Command c, string actor, Participant? p, string? requester, string? audience) =>
        IsOrganizeOp(c.Op) ? Task.Run(() => OrganizeCommand(c, actor, p, requester, audience)) : null;

    private void OrganizeStarted()
    {
        lock (organizeSync)
        {
            LoadPlan();
            // The companion stopped while applying: the step it was on is checked against Soundpad before anything else.
            if (orgPlan is { State: "applying" } plan) { plan.State = "paused"; plan.Paused = "restart"; plan.PausedText = "The companion stopped while applying. Each step is checked against Soundpad again; then Apply continues."; SavePlanLocked(); }
            // A step Soundpad went away during, in a plan that was paused or edited since: checked the same way.
            else if (Interrupted(orgPlan)) { orgPlan!.State = "paused"; orgPlan.Paused = "restart"; SavePlanLocked(); }
        }
        missingTimer = new Timer(_ => { try { RescanMissingFiles(); } catch (Exception ex) when (ex is not OutOfMemoryException) { } }, null, MissingFiles.RescanMs, MissingFiles.RescanMs);
    }
    private void OrganizeDispose()
    {
        try { applyCancel?.Cancel(); } catch (ObjectDisposedException) { }
        try { organizeStop.Cancel(); } catch (ObjectDisposedException) { }
        missingTimer?.Dispose(); missingFiles?.Dispose();
    }

    // ---- Missing files ----
    /// <summary>After each library refresh: a changed library is checked for missing files, and guided steps are re-checked.</summary>
    private void OrganizeLibraryRefreshed()
    {
        var current = parsed;
        if (current.Public.Version == organizeSeenVersion)
        {
            // Soundpad answered again with the same library: a step it went away during is still checked now.
            bool interrupted;
            lock (organizeSync) interrupted = orgLoaded && Interrupted(orgPlan);
            if (interrupted) _ = Task.Run(OrganizeLibraryChanged);
            return;
        }
        organizeSeenVersion = current.Public.Version;
        if (ScansFiles) Missing.Request(current.Public.Version, current.Paths);
        _ = Task.Run(OrganizeLibraryChanged);
    }
    private Library OrganizePublicLibrary(Library l)
    {
        var missing = missingFiles?.Missing;
        if (missing == null || missing.Count == 0) return l;
        long rev = missingFiles!.Revision;
        if (missingView is { } cached && cached.Version == l.Version && cached.Revision == rev) return cached.Result;
        var marked = l with
        {
            Sounds = [.. l.Sounds.Select(s => missing.Contains(s.Id) ? s with { Missing = true } : s)],
            // The version changes with the marks, so pages that cache the library by version see them.
            Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(l.Version + ":" + string.Join(",", missing.Order(StringComparer.Ordinal))))).ToLowerInvariant()[..24],
        };
        missingView = (l.Version, rev, marked);
        return marked;
    }
    private Reply? OrganizeMissingRefusal(Command c) =>
        c.SoundId != null && missingFiles?.Missing.Contains(c.SoundId) == true ? new("ack", c.Id, false, "The file is missing on the host's PC. Relink or remove this sound in Soundpad.") : null;

    // ---- The plan file ----
    private void LoadPlan()
    {
        if (orgLoaded) return;
        orgLoaded = true;
        var loaded = OrgFiles.LoadPlan();
        orgPlan = loaded.Plan; orgProblem = loaded.Problem; orgBlocked = loaded.Newer;
        if (orgProblem != null) Log(orgProblem);
    }
    private void SavePlanLocked()
    {
        if (orgPlan == null) return;
        // The revision moves only with a plan that was saved: a refused save leaves it as it was.
        orgPlan.Revision++;
        try { OrgFiles.SavePlan(orgPlan); }
        catch { orgPlan.Revision--; throw; }
        Interlocked.Increment(ref organizeRevision);
    }
    private void OrganizeChanged() { Interlocked.Increment(ref organizeRevision); Publish(); }
    /// <summary>The library as Soundpad last described it (the latest refresh), without asking Soundpad again.</summary>
    private NativeTree? CachedTree()
    {
        var soundlist = lastSoundlist; var categories = lastPlainCategories;
        if (soundlist == null || categories == null || !available) return null;
        var fingerprint = ManagedSoundpad.Hash(soundlist + "\n" + categories);
        if (cachedTree is { } c && c.Fingerprint == fingerprint) return c.Tree;
        try { var tree = NativeTree.Parse(soundlist, categories); cachedTree = (fingerprint, tree); return tree; }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return null; }
    }
    /// <summary>Soundpad's library now, read under the native gate (background lane).</summary>
    private async Task<NativeTree> LiveTree()
    {
        await gate.EnterAsync(PriorityGate.Lane.Background);
        try { return await NativeTree.Read(pipe); }
        finally { gate.Release(); }
    }
    /// <summary>Guided steps the host did in Soundpad are ticked off; a crash-interrupted step is checked again.</summary>
    private void OrganizeLibraryChanged()
    {
        try
        {
            var tree = CachedTree();
            if (tree == null) return;
            lock (organizeSync)
            {
                LoadPlan();
                if (orgPlan == null || orgBlocked || orgPlan.State == "applying") return;
                bool changed = false;
                foreach (var step in orgPlan.Steps) changed |= OrganizeRules.Watch(step, tree);
                // A removal the host did in Soundpad themselves.
                foreach (var step in orgPlan.Steps.Where(s => s.Kind == "removeCategory" && s.State == "pending" && s.Target != null && !tree.Categories.Any(s.Target.SamePlace))) { step.State = "done"; step.Note = "Seen done in Soundpad."; changed = true; }
                if (orgPlan.Paused == "restart" || Interrupted(orgPlan)) { orgPlan.State = "paused"; changed |= Reclassify(orgPlan, tree); }
                if (changed) { SavePlanLocked(); Publish(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Log("The organization plan could not be updated: " + ex.Message); }
    }
    /// <summary>
    /// After a restart mid-Apply: a step left "applying" is applied when its result is in Soundpad, pending (Retry)
    /// when Soundpad still looks as before it, and a conflict otherwise.
    /// </summary>
    private static bool Reclassify(OrganizePlan plan, NativeTree tree)
    {
        foreach (var step in plan.Steps.Where(s => s.State == "applying"))
        {
            bool post, pre;
            switch (step.Kind)
            {
                case "createCategory":
                    var made = tree.Categories.Where(n => n.Root && n.Name == step.Name).ToArray();
                    post = made.Length == 1; pre = made.Length == 0;
                    if (post) step.Created = made[0].Ref;
                    break;
                case "addSound":
                    var target = step.TargetStepId != null ? plan.Steps.FirstOrDefault(s => s.Id == step.TargetStepId)?.Created : step.Target;
                    var place = target == null ? [] : tree.Categories.Where(target.SamePlace).ToArray();
                    post = place.Length == 1 && place[0].Paths.Contains(step.SoundPath!, StringComparer.OrdinalIgnoreCase);
                    pre = place.Length == 1 && target!.Matches(place[0]);
                    break;
                case "removeCategory":
                    post = !tree.Categories.Any(step.Target!.SamePlace);
                    pre = tree.Categories.Count(step.Target!.Matches) == 1;
                    break;
                default: post = pre = false; break;
            }
            if (post) { step.State = "verified"; step.Note = "Checked after the restart: done in Soundpad."; }
            else if (pre) { step.State = "pending"; step.Note = "Checked after the restart: not done yet. Apply tries it again."; }
            else { step.State = "conflict"; step.Note = "Soundpad changed while the companion was stopped. Choose Retry, Skip or Rebind."; }
        }
        bool conflicts = plan.Steps.Any(s => s.State is "conflict" or "failed");
        if (conflicts) { plan.Paused = "conflict"; plan.PausedText = "Soundpad changed while the companion was stopped. Resolve the paused steps."; }
        else { plan.State = "draft"; plan.Paused = null; plan.PausedText = null; }
        return true;
    }

    // ---- Ops ----
    private void RequireGuestOrganize(Command c, Participant? p, string? requester, string? audience)
    {
        if (c.Op is not ("organizeGet" or "organizePropose" or "organizeWithdraw")) throw new InvalidDataException("Host permission required.");
        if (!OrgFiles.ReadSettings().GuestOrganize) throw new InvalidDataException("The host has not allowed organization proposals.");
        if (p == null || audioAudience != null && (!sessionAccepting || audience != audioAudience) || requester != null && revokedAudioPeers.ContainsKey(requester)) throw new InvalidDataException("This guest session authorization changed.");
    }
    private async Task<Reply> OrganizeCommand(Command c, string actor, Participant? p, string? requester, string? audience)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (c.V is not (1 or 2) || !Guid.TryParseExact(c.Id, "D", out _) || Math.Abs(now - c.At) > 30000) throw new InvalidDataException("Invalid or expired command.");
            bool host = actor == "host";
            if (!host) RequireGuestOrganize(c, p, requester, audience);
            lock (organizeReplies) if (organizeReplies.TryGetValue(c.Id, out var seen)) return seen;
            var reply = c.Op switch
            {
                "organizeGet" => Get(c, host, p),
                "organizeEdit" => await Edit(c),
                "organizeApply" => await StartApply(c),
                "organizeResolve" => await Resolve(c),
                "organizeDiscard" => Discard(c),
                "organizePropose" => await Propose(c, p!),
                "organizeWithdraw" => Withdraw(c, p!),
                "organizeSettings" => OrganizeSettingsOp(c),
                _ => throw new InvalidDataException("Unsupported command."),
            };
            if (c.Op != "organizeGet")
                lock (organizeReplies) { organizeReplies[c.Id] = reply; organizeReplyOrder.Enqueue(c.Id); while (organizeReplyOrder.Count > 256) organizeReplies.Remove(organizeReplyOrder.Dequeue()); }
            return reply;
        }
        catch (InvalidDataException ex) { return new("ack", c.Id, false, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Xml.XmlException)
        {
            return new("ack", c.Id, false, ex is IOException or OperationCanceledException or TimeoutException ? "Soundpad or the organization plan was unavailable. Check Soundpad, then try again." : "The organization plan could not be saved.");
        }
    }
    private void RequireWritablePlan(bool ignoreBusy = false)
    {
        if (orgBlocked) throw new InvalidDataException(orgProblem ?? "The organization plan can't be changed.");
        if (orgPlan is { State: "applying" } || !ignoreBusy && Volatile.Read(ref organizeBusy) != 0) throw new InvalidDataException("Wait until Apply finishes or pauses.");
    }
    /// <summary>A deep copy, so a change that is refused (limits, validation, a failed save) never stays in memory.</summary>
    private static OrganizePlan ClonePlan(OrganizePlan plan) => System.Text.Json.JsonSerializer.Deserialize<OrganizePlan>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan, Json.Options), Json.Options)!;
    private Reply Get(Command c, bool host, Participant? p)
    {
        if (c.Body != null) OrganizeRules.Body<OrganizeEmptyBody>(c);
        return new("ack", c.Id, true, Data: PlanView(host, host ? "host" : p!.Id));
    }
    private object PlanView(bool host, string viewer)
    {
        var tree = CachedTree();
        var current = parsed;
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, path) in current.Paths) byPath.TryAdd(path, id);
        string? SoundId(string path) => byPath.GetValueOrDefault(path);
        var settings = OrgFiles.ReadSettings();
        lock (organizeSync)
        {
            LoadPlan();
            var plan = orgPlan;
            var steps = plan?.Steps.Select(s => OrganizeRules.View(s, tree, SoundId, viewer)).ToArray() ?? [];
            var proposals = (plan?.Proposals ?? []).Where(x => host || x.ByActor == viewer).Select(x => new { x.Id, x.By, mine = x.ByActor == viewer, x.CreatedAt, steps = x.Steps.Select(s => OrganizeRules.View(s, tree, SoundId, viewer)).ToArray() }).ToArray();
            object? recovery = null; object? checkpoint = null;
            if (host && plan != null)
            {
                var created = plan.Steps.Count(s => s.Kind == "createCategory" && s.State == "verified" && s.Created != null);
                var added = plan.Steps.Where(s => s.Kind == "addSound" && s.State == "verified" && s.AddedToAllSounds != null).Select(s => new { soundName = s.SoundTitle, category = s.TargetStepId != null ? plan.Steps.FirstOrDefault(x => x.Id == s.TargetStepId)?.Name : s.Target?.Name }).ToArray();
                if (plan.Steps.Any(s => s.State is "verified") && plan.Steps.Any(s => s.State is "conflict" or "failed" or "pending" && OrganizeRules.IsNative(s.Kind))) recovery = new { created, added };
                if (OrgFiles.CheckpointCopy(plan.Id) is { } copy) checkpoint = new { copy, takenAtUtcMs = OrgFiles.CheckpointTaken(plan.Id) };
            }
            return new
            {
                revision = plan?.Revision ?? 0,
                plan = plan == null ? null : new { plan.Id, plan.Revision, plan.State, plan.Paused, plan.PausedText, waiting = applyWaiting && plan.State == "applying", steps, addBehaviour = plan.Measured.AddBehaviour },
                proposals,
                settings = new { settings.GuestOrganize, settings.AddBehaviour },
                problem = host ? orgProblem : null,
                recovery,
                checkpoint,
            };
        }
    }
    private OrganizePlan PlanForEdit()
    {
        RequireWritablePlan();
        orgPlan ??= new OrganizePlan();
        if (orgPlan.State == "done")
        {
            // A finished plan is cleared of what is finished when new changes come; open guided steps stay.
            orgPlan.Steps.RemoveAll(s => s.State is "verified" or "skipped" or "done");
            orgPlan.State = "draft"; orgPlan.Paused = null; orgPlan.PausedText = null;
        }
        return orgPlan;
    }
    /// <summary>
    /// Turns the website's inputs into steps against Soundpad's library now: sound ids and category ids only, each
    /// resolved here (a category's name must still match its id). Temp, kept-session and slot files, the app's own
    /// categories and All sounds are refused.
    /// </summary>
    private List<OrganizeStep> BuildSteps(IReadOnlyList<OrganizeStepInput> inputs, NativeTree tree, string by, string byActor, IReadOnlyList<OrganizeStep> existing)
    {
        var current = parsed; var owned = managed?.OwnedCategoryNames() ?? [];
        var keys = new Dictionary<string, OrganizeStep>(StringComparer.Ordinal);
        var result = new List<OrganizeStep>();
        NativeNode Category(OrganizeStepInput input)
        {
            if (input.CategoryId is not { Length: > 0 and <= 16 } id || !int.TryParse(id, out int index) || tree.ByIndex(index) is not { } node || node.Builtin) throw new InvalidDataException("That category is no longer in Soundpad. Refresh and choose it again.");
            if (input.CategoryName != null && input.CategoryName != node.Name || !OrganizeRules.SameParents(input.CategoryParents, node)) throw new InvalidDataException("Soundpad's categories changed. Refresh and choose the category again.");
            if (owned.Contains(node.Name)) throw new InvalidDataException("That category belongs to Soundpad Web (Temp or Saved). Choose another one.");
            return node;
        }
        (string Path, string Title) Sound(OrganizeStepInput input)
        {
            if (input.SoundId is not { Length: > 0 and <= 64 } id || !current.Paths.TryGetValue(id, out var path)) throw new InvalidDataException("That sound is no longer in Soundpad. Refresh and choose it again.");
            // Temp clips, the app's session files (managed-audio) and exact-playback slots are the app's, not the host's library.
            if (managed?.Annotation(id) is { Temporary: true } || SlotPool.IsSlotFile(path) || ManagedFiles.Beneath(path, Path.Combine(store.DirectoryPath, "managed-audio")))
                throw new InvalidDataException("That sound is in Temp. Keep it first, then organize it.");
            if (path.Any(ch => ch == '"' || char.IsControl(ch))) throw new InvalidDataException("That sound's file name can't be sent to Soundpad.");
            return (path, current.Public.Sounds.FirstOrDefault(s => s.Id == id)?.Name ?? Path.GetFileNameWithoutExtension(path));
        }
        foreach (var input in inputs)
        {
            var step = new OrganizeStep { Kind = input.Kind, By = by, ByActor = byActor };
            switch (input.Kind)
            {
                case "createCategory":
                case "createNested":
                case "renameCategory":
                    if (OrganizeFiles.NameProblem(input.Name) is { } problem) throw new InvalidDataException(problem);
                    step.Name = input.Name;
                    if (input.Kind != "createCategory") step.Target = Category(input).Ref;
                    if (input.Kind == "createCategory" && tree.Categories.Any(n => n.Root && n.Name == input.Name) || input.Kind == "createCategory" && existing.Concat(result).Any(s => s.Kind == "createCategory" && s.Name == input.Name && s.State is not ("skipped" or "failed"))) throw new InvalidDataException($"A category named “{input.Name}” already exists. Choose another name.");
                    if (input.Kind == "createCategory" && input.Key is { Length: > 0 and <= 32 } key) keys[key] = step;
                    step.State = input.Kind == "createCategory" ? "pending" : "manual";
                    break;
                case "addSound":
                case "removeFromCategory":
                    (step.SoundPath, step.SoundTitle) = Sound(input);
                    if (input.Kind == "addSound" && (input.TargetStepId != null || input.TargetKey != null))
                    {
                        var made = input.TargetKey != null ? keys.GetValueOrDefault(input.TargetKey) : existing.FirstOrDefault(s => s.Id == input.TargetStepId && s.Kind == "createCategory");
                        step.TargetStepId = made?.Id ?? throw new InvalidDataException("Choose the new category again.");
                    }
                    else
                    {
                        var node = Category(input);
                        bool inside = node.Paths.Contains(step.SoundPath, StringComparer.OrdinalIgnoreCase);
                        if (input.Kind == "addSound" && inside) continue; // already there: nothing to do
                        if (input.Kind == "removeFromCategory" && !inside) continue;
                        step.Target = node.Ref;
                    }
                    step.State = input.Kind == "addSound" ? "pending" : "manual";
                    break;
                case "removeCategory":
                case "reorder":
                    step.Target = Category(input).Ref;
                    if (input.Kind == "reorder" && input.SoundId != null) (step.SoundPath, step.SoundTitle) = Sound(input);
                    step.State = input.Kind == "removeCategory" ? "pending" : "manual";
                    break;
                default: throw new InvalidDataException("Unsupported organization step.");
            }
            // The same change twice is kept once.
            if (existing.Concat(result).Any(s => s.State is not ("skipped" or "failed" or "done" or "verified") && s.Kind == step.Kind && s.Name == step.Name && s.SoundPath == step.SoundPath && s.TargetStepId == step.TargetStepId && (s.Target?.Name, s.Target?.PathsHash) == (step.Target?.Name, step.Target?.PathsHash) && s.Target?.Chain.SequenceEqual(step.Target?.Chain ?? []) != false)) continue;
            result.Add(step);
        }
        return result;
    }
    private async Task<Reply> Edit(Command c)
    {
        var body = OrganizeRules.Body<OrganizeEditBody>(c);
        if (body.Add?.Length > OrganizeFiles.MaxProposalSteps || body.Remove?.Length > OrganizeFiles.MaxSteps) throw new InvalidDataException("Too many changes at once.");
        var tree = body.Add is { Length: > 0 } || body.AcceptProposal != null ? await LiveTree() : null;
        lock (organizeSync)
        {
            LoadPlan();
            if (body.PlanRevision != (orgPlan?.Revision ?? 0)) throw new InvalidDataException("The plan changed meanwhile. Review it again.");
            // The edit is made on a copy; the plan in memory changes only once the copy is checked and saved.
            var original = orgPlan;
            if (original != null) orgPlan = ClonePlan(original);
            try { EditLocked(body, tree); }
            catch { orgPlan = original; throw; }
        }
        Publish();
        return new("ack", c.Id, true, Data: PlanView(true, "host"));
    }
    private void EditLocked(OrganizeEditBody body, NativeTree? tree)
    {
        var plan = PlanForEdit();
        if (body.Remove is { } remove)
        {
            plan.Steps.RemoveAll(s => remove.Contains(s.Id) && s.State is not ("verified" or "applying"));
            plan.Steps.RemoveAll(s => s.TargetStepId != null && !plan.Steps.Any(x => x.Id == s.TargetStepId));
        }
        if (body.Add is { Length: > 0 } add) plan.Steps.AddRange(BuildSteps(add, tree!, "Host", "host", plan.Steps));
        if (body.AcceptProposal is { } accept)
        {
            var proposal = plan.Proposals.FirstOrDefault(x => x.Id == accept) ?? throw new InvalidDataException("That proposal was withdrawn.");
            foreach (var step in proposal.Steps) { step.ProposalId = proposal.Id; if (step.State is not ("pending" or "manual")) step.State = OrganizeRules.IsNative(step.Kind) ? "pending" : "manual"; }
            plan.Steps.AddRange(proposal.Steps.Where(s => !plan.Steps.Any(x => x.Id == s.Id)));
            plan.Proposals.Remove(proposal);
        }
        if (body.RejectProposal is { } reject) plan.Proposals.RemoveAll(x => x.Id == reject);
        if (plan.Steps.Count > OrganizeFiles.MaxSteps) throw new InvalidDataException($"A plan holds at most {OrganizeFiles.MaxSteps} changes. Apply or discard some first.");
        // An edit never answers a question for the host: the duplicates question and an interrupted step stay.
        if (plan.State == "paused" && plan.Paused is not ("duplicates" or "restart") && !plan.Steps.Any(s => s.State is "conflict" or "failed" or "applying")) { plan.State = "draft"; plan.Paused = null; plan.PausedText = null; }
        if (plan.Steps.Count == 0 && plan.Proposals.Count == 0) { OrgFiles.DeletePlan(); orgPlan = null; Interlocked.Increment(ref organizeRevision); }
        else { plan.BaseFingerprint = tree?.Fingerprint ?? plan.BaseFingerprint; SavePlanLocked(); }
    }
    private async Task<Reply> Propose(Command c, Participant p)
    {
        var body = OrganizeRules.Body<OrganizeProposeBody>(c);
        if (body.Steps.Length is 0 or > OrganizeFiles.MaxProposalSteps) throw new InvalidDataException($"A proposal holds 1 to {OrganizeFiles.MaxProposalSteps} changes.");
        var tree = await LiveTree();
        string id;
        lock (organizeSync)
        {
            LoadPlan();
            if (orgBlocked) throw new InvalidDataException("The host's organization plan can't take proposals now.");
            var plan = orgPlan ?? new OrganizePlan();
            if (plan.Proposals.Count(x => x.ByActor == p.Id) >= OrganizeFiles.MaxProposalsPerGuest) throw new InvalidDataException($"You have {OrganizeFiles.MaxProposalsPerGuest} proposals waiting. Withdraw one first.");
            if (plan.Proposals.Count >= OrganizeFiles.MaxProposals) throw new InvalidDataException("The host has too many proposals waiting. Try again once the host has answered some.");
            var name = p.Name.Length is > 0 and <= 40 ? p.Name : "Guest";
            var steps = BuildSteps(body.Steps, tree, name, p.Id, []);
            if (steps.Count == 0) throw new InvalidDataException("Nothing to propose: those changes are already in Soundpad.");
            var proposal = new OrganizeProposal { By = name, ByActor = p.Id, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Steps = steps };
            foreach (var step in steps) step.ProposalId = proposal.Id;
            var before = orgPlan;
            orgPlan = plan; plan.Proposals.Add(proposal); id = proposal.Id;
            // A refused save takes the proposal out again, so it never reaches the host's plan later.
            try { SavePlanLocked(); }
            catch { plan.Proposals.Remove(proposal); orgPlan = before; throw; }
        }
        Publish();
        return new("ack", c.Id, true, Data: new { proposalId = id });
    }
    private Reply Withdraw(Command c, Participant p)
    {
        var body = OrganizeRules.Body<OrganizeWithdrawBody>(c);
        lock (organizeSync)
        {
            LoadPlan();
            if (orgPlan == null || orgBlocked || orgPlan.Proposals.RemoveAll(x => x.Id == body.ProposalId && x.ByActor == p.Id) == 0) throw new InvalidDataException("That proposal is not waiting any more.");
            if (orgPlan.Steps.Count == 0 && orgPlan.Proposals.Count == 0) { OrgFiles.DeletePlan(); orgPlan = null; Interlocked.Increment(ref organizeRevision); }
            else SavePlanLocked();
        }
        Publish();
        return new("ack", c.Id, true);
    }
    private Reply Discard(Command c)
    {
        if (c.Body != null) OrganizeRules.Body<OrganizeEmptyBody>(c);
        lock (organizeSync)
        {
            LoadPlan();
            RequireWritablePlan();
            if (orgPlan == null) return new("ack", c.Id, true);
            // Guests' proposals stay: only the host's plan goes.
            if (orgPlan.Proposals.Count == 0) { OrgFiles.DeletePlan(); orgPlan = null; Interlocked.Increment(ref organizeRevision); }
            else
            {
                var original = orgPlan;
                orgPlan = new OrganizePlan { Proposals = original.Proposals, Revision = original.Revision };
                try { SavePlanLocked(); } catch { orgPlan = original; throw; }
            }
        }
        Publish();
        return new("ack", c.Id, true, Data: PlanView(true, "host"));
    }
    private Reply OrganizeSettingsOp(Command c)
    {
        var body = OrganizeRules.Body<OrganizeSettingsBody>(c);
        var next = OrgFiles.UpdateSettings(guestOrganize: body.GuestOrganize);
        OrganizeChanged();
        return new("ack", c.Id, true, Data: new { next.GuestOrganize, next.AddBehaviour });
    }
    /// <summary>The guestOrganize permission (2.44), read from organize\settings.v1.json for the snapshot.</summary>
    private bool GuestOrganizeSetting { get { try { return OrgFiles.ReadSettings().GuestOrganize; } catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } } }
    /// <summary>A permissions command from a 2.44 website may carry guestOrganize; it is stored in the organize settings.</summary>
    private void ApplyGuestOrganizePermission(bool? value)
    {
        if (value is not bool allowed || allowed == GuestOrganizeSetting) return;
        try { OrgFiles.UpdateSettings(guestOrganize: allowed); Interlocked.Increment(ref organizeRevision); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Log("The organize setting could not be saved: " + ex.Message); }
    }
}
