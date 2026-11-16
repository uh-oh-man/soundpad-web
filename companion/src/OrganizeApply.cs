namespace SoundpadWeb;

// Organize Apply (2.44): the host's confirmed plan, step by step, in Soundpad. Every step waits for the quiet rule,
// re-reads Soundpad, checks its own precondition, is recorded as "applying" before its one command is sent, and is
// verified against exactly the change it should make. Anything unexpected pauses the plan and keeps it.
public sealed partial class Bridge
{
    private CancellationTokenSource? applyCancel;
    private Task? applyTask;
    private volatile bool applyWaiting;
    /// <summary>Tests: runs inside the gate entry of each step, before its command is sent (step kind).</summary>
    private Func<string, Task>? beforeOrganizeCommand;
    internal void UseBeforeOrganizeCommand(Func<string, Task> hook) => beforeOrganizeCommand = hook;
    /// <summary>Tests: the quiet second Apply waits for (default <see cref="QuietRule.QuietMs"/>).</summary>
    internal int OrganizeQuietMs { get; set; } = QuietRule.QuietMs;
    internal Task OrganizeApplyIdle() => applyTask ?? Task.CompletedTask;
    /// <summary>
    /// 1 while Apply is starting (its Soundpad reads, checkpoint and backup, before the plan says "applying") or the
    /// categories a plan created are being removed again. Set under <see cref="stageHExclusive"/>, so an update or a
    /// whole-library restore can't start meanwhile, and the host's plan can't be edited.
    /// </summary>
    private int organizeBusy;
    /// <summary>Stops a waiting "remove the categories this plan created" when the companion closes.</summary>
    private readonly CancellationTokenSource organizeStop = new();
    /// <summary>How long removing created categories waits for Soundpad to be quiet before it keeps the rest (tests shorten it).</summary>
    internal int OrganizeRemoveWaitMs { get; set; } = 120000;
    /// <summary>True while a plan is being applied to Soundpad or Apply is starting (an update or a restore is refused then: BridgeStageH.cs).</summary>
    private bool OrganizeApplying { get { if (Volatile.Read(ref organizeBusy) != 0) return true; lock (organizeSync) return orgPlan?.State == "applying"; } }
    /// <summary>Marks Organize busy, unless an update installs, a whole-library restore runs or Organize is busy already.</summary>
    private void EnterOrganizeBusy()
    {
        lock (stageHExclusive)
        {
            if (UpdateBusy is { } updating) throw new InvalidDataException(updating);
            if (restoringLibrary) throw new InvalidDataException("The host is restoring the library from a backup. Apply when it has finished.");
            if (Interlocked.CompareExchange(ref organizeBusy, 1, 0) != 0) throw new InvalidDataException("Soundpad Web is already working on this plan. Wait until it finishes or pauses.");
        }
    }
    /// <summary>A step left "applying" while the plan is not being applied: Soundpad went away mid-step, and it was not checked since.</summary>
    private static bool Interrupted(OrganizePlan? plan) => plan is { State: not "applying" } && plan.Steps.Any(s => s.State == "applying");
    /// <summary>Checks an interrupted step against Soundpad now (as after a restart), so nothing is sent over a step whose result is unknown.</summary>
    private async Task RecheckInterrupted()
    {
        var tree = await LiveTree();
        lock (organizeSync)
        {
            if (!Interrupted(orgPlan)) return;
            orgPlan!.State = "paused"; orgPlan.Paused = "restart";
            Reclassify(orgPlan, tree);
            SavePlanLocked();
        }
        OrganizeChanged();
    }
    /// <summary>Runs one batch under the native gate in the background lane, so a ready play goes first.</summary>
    private async Task GateBatch(Func<Task> work)
    {
        await gate.EnterAsync(PriorityGate.Lane.Background);
        try { await work(); } finally { gate.Release(); }
    }
    private enum StepOutcome { Verified, NotNow, Conflict, Failed }

    private async Task<Reply> StartApply(Command c)
    {
        var body = OrganizeRules.Body<OrganizeApplyBody>(c);
        if (!available) throw new InvalidDataException("Soundpad is unavailable. Open it, then Apply.");
        // A step Soundpad went away during is checked first: nothing is sent while its result is unknown.
        bool interrupted;
        lock (organizeSync) { LoadPlan(); interrupted = Interrupted(orgPlan); }
        if (interrupted)
        {
            await RecheckInterrupted();
            throw new InvalidDataException("A change that was interrupted was checked against Soundpad again. Review the plan, then Apply.");
        }
        // Not while an update installs or a whole-library restore runs, and neither of those can start from here
        // until the plan says "applying" (Stage H cross-checks, BridgeStageH.cs). Edits of the plan wait meanwhile.
        EnterOrganizeBusy();
        try
        {
            string planId;
            lock (organizeSync)
            {
                LoadPlan();
                RequireWritablePlan(ignoreBusy: true);
                if (orgPlan == null || orgPlan.Id != body.PlanId || orgPlan.Revision != body.PlanRevision) throw new InvalidDataException("The plan changed meanwhile. Review it again.");
                if (orgPlan.Steps.Any(s => s.State is "conflict" or "failed")) throw new InvalidDataException("Resolve the paused steps first: Skip, Retry or Rebind.");
                if (!orgPlan.Steps.Any(s => s.State == "pending" && OrganizeRules.IsNative(s.Kind))) throw new InvalidDataException("Nothing for Soundpad Web to apply: the remaining steps are done in Soundpad.");
                planId = orgPlan.Id;
            }
            // A checkpoint before Soundpad first changes for this plan: the tree and a copy of soundlist.spl (never
            // written back by the app). A later Apply of the same plan keeps the first one.
            string? copy = null;
            try
            {
                copy = OrgFiles.CheckpointCopy(planId);
                if (!OrgFiles.HasCheckpoint(planId))
                {
                    string soundlist = "", categories = "";
                    await GateBatch(async () => { soundlist = await pipe.Request("GetSoundlist()"); categories = await pipe.Request("GetCategories(true, false)"); });
                    copy = OrgFiles.WriteCheckpoint(planId, soundlist, categories, LeppsoftFolder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("The organize checkpoint could not be written: " + ex.Message); }
            // And a backup first when a backup folder is set (WP1); without one Apply goes on and says so.
            string backupNote;
            try
            {
                var backup = await CheckpointH("pre-organize", CancellationToken.None);
                backupNote = backup is { Skipped: false } ? "A backup was made first." : "No backup was made: " + (backup?.Reason ?? "Backups are not set up.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { backupNote = "No backup was made: " + ex.Message; }
            Log("Organize: " + backupNote);
            lock (organizeSync)
            {
                if (orgPlan?.Id != planId || orgPlan.State is "applying" or "done") throw new InvalidDataException("The plan changed meanwhile. Review it again.");
                orgPlan.State = "applying"; orgPlan.Paused = null; orgPlan.PausedText = null;
                SavePlanLocked();
                applyCancel?.Dispose(); applyCancel = new CancellationTokenSource();
                var token = applyCancel.Token;
                applyTask = Task.Run(() => RunApply(planId, token));
            }
            Publish();
            return new("ack", c.Id, true, Data: new { started = true, checkpoint = copy != null, backup = backupNote });
        }
        finally { Volatile.Write(ref organizeBusy, 0); }
    }

    private async Task RunApply(string planId, CancellationToken cancel)
    {
        // Steps Soundpad can't do yet (a category that still holds sounds) stay pending for a later Apply.
        var later = new HashSet<string>();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                OrganizeStep? next;
                lock (organizeSync) next = orgPlan?.Id != planId ? null : orgPlan.Steps.FirstOrDefault(s => s.State == "pending" && OrganizeRules.IsNative(s.Kind) && !later.Contains(s.Id));
                if (next == null) break;
                StepOutcome outcome;
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var (quiet, result) = await QuietRule.TryOnce(GateBatch, pipe, () => Interlocked.Read(ref lastAudio), () => ApplyStep(planId, next.Id), OrganizeQuietMs, cancel);
                    if (quiet == QuietRule.Outcome.Ran) { outcome = result; break; }
                    if (!applyWaiting) { applyWaiting = true; OrganizeChanged(); }
                    await Task.Delay(300, cancel);
                }
                if (applyWaiting) { applyWaiting = false; OrganizeChanged(); }
                if (outcome is StepOutcome.Conflict or StepOutcome.Failed) { Pause(planId, outcome == StepOutcome.Conflict ? "conflict" : "failed", outcome == StepOutcome.Conflict ? "Soundpad changed since the plan was made. The plan is kept; resolve the paused step." : "A change did not turn out as expected in Soundpad. Nothing else was sent; check Soundpad, then resolve the step."); return; }
                if (outcome == StepOutcome.NotNow) { later.Add(next.Id); continue; }
                // Soundpad may have renumbered categories: the app's Temp and Saved categories follow by name, and
                // every page sees the new library at once.
                await RebindOwned();
                await RefreshAfterOrganize();
                bool duplicates;
                lock (organizeSync) duplicates = orgPlan?.Id == planId && orgPlan.Paused == "duplicates";
                OrganizeChanged();
                if (duplicates) return;
            }
            cancel.ThrowIfCancellationRequested();
            lock (organizeSync)
            {
                if (orgPlan?.Id == planId && orgPlan.State == "applying")
                {
                    // Never "done" over a step whose result is unknown: it is checked against Soundpad again first.
                    if (orgPlan.Steps.Any(s => s.State == "applying")) { orgPlan.State = "paused"; orgPlan.Paused = "restart"; orgPlan.PausedText = "A change was interrupted. It is checked against Soundpad again; then Apply continues."; }
                    else orgPlan.State = orgPlan.Steps.Any(s => s.State == "pending" && OrganizeRules.IsNative(s.Kind)) ? "draft" : "done";
                    SavePlanLocked();
                }
            }
            OrganizeChanged();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { applyWaiting = false; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or InvalidDataException or System.Xml.XmlException)
        {
            applyWaiting = false;
            Pause(planId, "soundpad", "Soundpad stopped answering during Apply. The plan is kept; open Soundpad, check the steps, then Apply again.");
        }
    }
    private async Task RefreshAfterOrganize()
    {
        try { await GateBatch(RefreshInner); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        Publish();
    }
    private async Task RebindOwned()
    {
        if (managed == null) return;
        try { await audioGate.EnterAsync(PriorityGate.Lane.Background); try { await managed.RebindOwnedCategories(); } finally { audioGate.Release(); } }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or System.Xml.XmlException) { }
    }
    private void Pause(string planId, string why, string text)
    {
        try
        {
            lock (organizeSync)
            {
                if (orgPlan?.Id != planId) return;
                orgPlan.State = "paused"; orgPlan.Paused = why; orgPlan.PausedText = text;
                // A step left "applying" (Soundpad gone mid-step) is checked again when the library is next read.
                if (orgPlan.Steps.Any(s => s.State == "applying")) orgPlan.Paused = "restart";
                SavePlanLocked();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Log("The organization plan could not be saved: " + ex.Message); }
        OrganizeChanged();
    }

    /// <summary>One step, inside one gate entry: read, check, record, send, verify.</summary>
    private async Task<StepOutcome> ApplyStep(string planId, string stepId)
    {
        var before = await NativeTree.Read(pipe);
        string command; Func<NativeTree, bool> expected; OrganizeStep step; string? path = null; NativeNode? target = null;
        var owned = managed?.OwnedCategoryNames() ?? [];
        lock (organizeSync)
        {
            if (orgPlan?.Id != planId) return StepOutcome.NotNow;
            step = orgPlan.Steps.First(s => s.Id == stepId);
            StepOutcome Conflict(string note, CategoryRef? candidate = null) { step.State = "conflict"; step.Note = note; step.Candidate = candidate; SavePlanLocked(); return StepOutcome.Conflict; }
            StepOutcome Failed(string note) { step.State = "failed"; step.Note = note; SavePlanLocked(); return StepOutcome.Failed; }
            var beforeSignatures = before.Signatures(); var beforeSounds = before.SoundPaths();
            switch (step.Kind)
            {
                case "createCategory":
                    if (OrganizeFiles.NameProblem(step.Name) is { } problem) return Failed(problem);
                    if (before.Categories.Any(n => n.Root && n.Name == step.Name)) return Conflict($"A top-level category named “{step.Name}” is already in Soundpad. Skip this step or discard it and choose another name.");
                    command = $"DoAddCategory({ManagedSoundpad.Quote(step.Name!)}, -1)";
                    var made = NativeTree.Signature([], step.Name!, []);
                    expected = after => Same(after.Signatures(), [.. beforeSignatures, made]) && after.SoundPaths().SequenceEqual(beforeSounds);
                    break;
                case "addSound":
                    path = step.SoundPath!;
                    if (before.Count(path) == 0) return Conflict($"“{step.SoundTitle}” is no longer in Soundpad. Skip this step.");
                    var reference = step.TargetStepId != null ? orgPlan.Steps.FirstOrDefault(s => s.Id == step.TargetStepId) is { State: "verified", Created: { } created } ? created : null : step.Target;
                    if (reference == null) return Conflict("Its new category was not created. Skip this step, or retry once the category step is applied.");
                    target = before.Find(reference, out var candidate);
                    if (target == null) return Conflict(candidate != null ? $"“{reference.Name}” changed in Soundpad since the plan was made. Confirm it is still the right category (Rebind), or Skip." : $"“{reference.Name}” is no longer in Soundpad as it was. Rebind to another category, or Skip.", candidate?.Ref);
                    if (target.Builtin || owned.Contains(target.Name)) return Failed("That category belongs to Soundpad Web or Soundpad itself.");
                    if (target.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) { step.State = "verified"; step.Note = "Already in that category."; step.AddedToAllSounds = null; SavePlanLocked(); return StepOutcome.Verified; }
                    command = $"DoAddSound({ManagedSoundpad.Quote(path)}, {target.Index}, -1)";
                    var grown = NativeTree.Signature(target.Chain, target.Name, [.. target.Paths, path]);
                    var oldSignature = NativeTree.Signature(target);
                    var expectedSignatures = new List<string>(beforeSignatures); expectedSignatures.Remove(oldSignature); expectedSignatures.Add(grown);
                    var withDuplicate = new List<string>(beforeSounds) { path.Replace('/', '\\').ToUpperInvariant() }; withDuplicate.Sort(StringComparer.Ordinal);
                    // Only once All sounds lists the category's entry: Soundpad shows the category first, and a read in
                    // between would look like "no second entry" and measure the wrong add behaviour.
                    var addedPath = path;
                    expected = after => Same(after.Signatures(), expectedSignatures) && after.Listed(addedPath) && (after.SoundPaths().SequenceEqual(beforeSounds) || after.SoundPaths().SequenceEqual(withDuplicate));
                    break;
                case "removeCategory":
                    target = before.Find(step.Target!, out var moved);
                    if (target == null)
                    {
                        if (!before.Categories.Any(step.Target!.SamePlace)) { step.State = "done"; step.Note = "Already gone from Soundpad."; SavePlanLocked(); return StepOutcome.NotNow; }
                        return Conflict($"“{step.Target!.Name}” changed in Soundpad since the plan was made. Rebind or Skip.", moved?.Ref);
                    }
                    if (target.Builtin || owned.Contains(target.Name)) return Failed("That category belongs to Soundpad Web or Soundpad itself.");
                    // Never a category with sounds or subcategories: Soundpad would take its sounds out of All sounds.
                    if (!target.Root || target.Children.Length > 0 || target.Paths.Length > 0) { step.Note = "Waiting: " + OrganizeRules.Classify(step, before).Reason; SavePlanLocked(); return StepOutcome.NotNow; }
                    command = $"DoRemoveCategory({target.Index})";
                    var gone = NativeTree.Signature(target);
                    var remaining = new List<string>(beforeSignatures); remaining.Remove(gone);
                    expected = after => Same(after.Signatures(), remaining) && after.SoundPaths().SequenceEqual(beforeSounds);
                    break;
                default: return StepOutcome.NotNow;
            }
            // Durable before the command: a crash from here on is checked again at the next start.
            step.State = "applying"; step.Note = null; step.Candidate = null; SavePlanLocked();
        }
        Publish();
        if (beforeOrganizeCommand != null) await beforeOrganizeCommand(step.Kind);
        string answer = await pipe.Request(command);
        NativeTree? after = null; bool ok = false;
        if (answer.StartsWith("R-200", StringComparison.Ordinal))
        {
            // Soundpad applies changes a moment later on its own thread: read again until it shows the one expected change.
            for (int attempt = 0; attempt < 20 && !ok; attempt++)
            {
                if (attempt > 0) await Task.Delay(150);
                after = await NativeTree.Read(pipe);
                ok = expected(after);
            }
        }
        lock (organizeSync)
        {
            if (orgPlan?.Id != planId || orgPlan.Steps.FirstOrDefault(s => s.Id == stepId) is not { } current) return StepOutcome.NotNow;
            step = current;
            if (!ok)
            {
                step.State = "failed";
                step.Note = answer.StartsWith("R-200", StringComparison.Ordinal) ? "Soundpad's result was not the one expected (it may have been changed meanwhile). Nothing else was sent; check it in Soundpad." : "Soundpad refused this change. Nothing else was sent.";
                SavePlanLocked();
                return StepOutcome.Failed;
            }
            step.State = "verified"; step.Note = null;
            if (step.Kind == "createCategory")
            {
                var made = after!.Categories.Where(n => n.Root && n.Name == step.Name && n.Paths.Length == 0 && n.Children.Length == 0).ToArray();
                step.Created = made.Length == 1 ? made[0].Ref : null;
            }
            if (step.Kind == "addSound" && target != null)
            {
                bool added = after!.Count(path!) > before.Count(path!);
                step.AddedToAllSounds = added;
                // Later steps aimed at that category follow its new contents.
                var grownRef = target.Ref with { PathsHash = NativeTree.HashPaths([.. target.Paths, path!]) };
                foreach (var other in orgPlan.Steps.Where(s => s != step && s.State is "pending" or "manual"))
                {
                    if (other.Target != null && other.Target.Matches(target)) other.Target = grownRef;
                }
                if (step.TargetStepId != null && orgPlan.Steps.FirstOrDefault(s => s.Id == step.TargetStepId) is { Created: { } c } maker && c.Matches(target)) maker.Created = grownRef;
                // The first add on this Soundpad measures whether All sounds gains a second entry; duplicates pause the plan.
                var settings = OrgFiles.ReadSettings();
                if (settings.AddBehaviour == "unknown")
                {
                    var behaviour = added ? "duplicates" : "noDuplicate";
                    try { OrgFiles.UpdateSettings(addBehaviour: behaviour); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    orgPlan.Measured.AddBehaviour = behaviour;
                    if (added)
                    {
                        step.Note = "Soundpad listed this sound a second time in All sounds.";
                        orgPlan.State = "paused"; orgPlan.Paused = "duplicates";
                        orgPlan.PausedText = $"Adding “{step.SoundTitle}” to a category made Soundpad list it twice in All sounds. Continue (each sound added will show once more in All sounds), or Stop and remove the extra entry in Soundpad.";
                    }
                }
                else if (orgPlan.Measured.AddBehaviour == "unknown") orgPlan.Measured.AddBehaviour = settings.AddBehaviour;
            }
            SavePlanLocked();
            return StepOutcome.Verified;
        }
    }
    private static bool Same(List<string> actual, List<string> expected)
    {
        var sorted = new List<string>(expected); sorted.Sort(StringComparer.Ordinal);
        return actual.SequenceEqual(sorted);
    }

    private async Task<Reply> Resolve(Command c)
    {
        var body = OrganizeRules.Body<OrganizeResolveBody>(c);
        if (body.Resolution == "removeCreated") return await RemoveCreated(c);
        NativeTree? tree = body.Resolution == "rebind" ? await LiveTree() : null;
        bool restart = false;
        lock (organizeSync)
        {
            LoadPlan();
            if (orgBlocked || orgPlan == null) throw new InvalidDataException("There is no organization plan.");
            var plan = orgPlan;
            if (body.Resolution == "cancel")
            {
                try { applyCancel?.Cancel(); } catch (ObjectDisposedException) { }
                foreach (var s in plan.Steps.Where(s => s.State is "pending" or "conflict" or "failed")) { s.State = "skipped"; s.Note = "Cancelled."; }
                plan.State = "done"; plan.Paused = null; plan.PausedText = null;
                SavePlanLocked();
            }
            else if (body.Resolution is "continue" or "stop")
            {
                if (plan.Paused != "duplicates") throw new InvalidDataException("Nothing is waiting for that answer.");
                plan.State = "draft"; plan.Paused = null; plan.PausedText = null;
                SavePlanLocked();
                restart = body.Resolution == "continue";
            }
            else
            {
                RequireWritablePlan();
                var step = plan.Steps.FirstOrDefault(s => s.Id == body.StepId) ?? throw new InvalidDataException("That step is not in the plan any more.");
                switch (body.Resolution)
                {
                    case "skip":
                        if (step.State is "verified" or "applying") throw new InvalidDataException("That step is already applied.");
                        step.State = "skipped"; step.Note = "Skipped."; break;
                    case "retry":
                        if (step.State is not ("conflict" or "failed" or "skipped")) throw new InvalidDataException("That step does not need a retry.");
                        step.State = OrganizeRules.IsNative(step.Kind) ? "pending" : "manual"; step.Note = null; step.Candidate = null; break;
                    case "done":
                        if (step.State is not ("manual" or "flagged")) throw new InvalidDataException("Only steps done in Soundpad can be marked done.");
                        step.State = "done"; step.Note = "Marked done."; break;
                    case "rebind":
                        if (step.Kind == "createCategory" || step.TargetStepId != null) throw new InvalidDataException("That step has no category to rebind.");
                        if (body.TargetCategoryId is not { Length: > 0 and <= 16 } id || !int.TryParse(id, out int index) || tree!.ByIndex(index) is not { Builtin: false } node || body.TargetCategoryName != null && body.TargetCategoryName != node.Name || !OrganizeRules.SameParents(body.TargetCategoryParents, node)) throw new InvalidDataException("That category is no longer in Soundpad. Refresh and choose it again.");
                        if ((managed?.OwnedCategoryNames() ?? []).Contains(node.Name)) throw new InvalidDataException("That category belongs to Soundpad Web (Temp or Saved). Choose another one.");
                        step.Target = node.Ref; step.Candidate = null; step.Note = "Rebound to “" + node.Name + "”.";
                        step.State = OrganizeRules.IsNative(step.Kind) ? "pending" : "manual"; break;
                    default: throw new InvalidDataException("Unsupported resolution.");
                }
                if (plan.State == "paused" && !plan.Steps.Any(s => s.State is "conflict" or "failed" or "applying")) { plan.State = "draft"; plan.Paused = null; plan.PausedText = null; }
                SavePlanLocked();
            }
        }
        Publish();
        if (restart)
        {
            long revision; string id;
            lock (organizeSync) { revision = orgPlan!.Revision; id = orgPlan.Id; }
            if (orgPlan.Steps.Any(s => s.State == "pending" && OrganizeRules.IsNative(s.Kind)))
                await StartApply(c with { Body = System.Text.Json.JsonSerializer.SerializeToElement(new { planId = id, planRevision = revision }, Json.Options) });
        }
        return new("ack", c.Id, true, Data: PlanView(true, "host"));
    }

    /// <summary>
    /// Recovery after a partial apply: the empty top-level categories this plan created are removed again, natively,
    /// behind the quiet rule and verified. Categories something was added to since are left (guided instead).
    /// </summary>
    private async Task<Reply> RemoveCreated(Command c)
    {
        if (!available) throw new InvalidDataException("Soundpad is unavailable. Open it, then try again.");
        // The same refusals as Apply, and Apply can't start while this runs.
        EnterOrganizeBusy();
        try
        {
            List<(string Id, CategoryRef Created)> made;
            lock (organizeSync)
            {
                LoadPlan();
                RequireWritablePlan(ignoreBusy: true);
                made = orgPlan?.Steps.Where(s => s.Kind == "createCategory" && s.State == "verified" && s.Created != null).Select(s => (s.Id, s.Created!)).ToList() ?? [];
            }
            int removed = 0, kept = 0;
            // While Soundpad keeps playing this waits, but not for ever: what is left then is kept (and said).
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(organizeStop.Token);
            stop.CancelAfter(OrganizeRemoveWaitMs);
            foreach (var (stepId, created) in made)
            {
                bool? result = null;
                while (result == null && !stop.IsCancellationRequested)
                {
                    try
                    {
                        var (quiet, ran) = await QuietRule.TryOnce(GateBatch, pipe, () => Interlocked.Read(ref lastAudio), async () =>
                        {
                            var before = await NativeTree.Read(pipe);
                            var node = before.Categories.Where(n => n.Root && created.SamePlace(n)).ToArray();
                            if (node.Length != 1 || node[0].Paths.Length > 0 || node[0].Children.Length > 0) return false;
                            var remaining = before.Signatures(); remaining.Remove(NativeTree.Signature(node[0])); var sounds = before.SoundPaths();
                            if (!(await pipe.Request($"DoRemoveCategory({node[0].Index})")).StartsWith("R-200", StringComparison.Ordinal)) return false;
                            for (int attempt = 0; attempt < 20; attempt++)
                            {
                                if (attempt > 0) await Task.Delay(150);
                                var after = await NativeTree.Read(pipe);
                                if (after.Signatures().SequenceEqual(remaining) && after.SoundPaths().SequenceEqual(sounds)) return true;
                            }
                            return false;
                        }, OrganizeQuietMs, stop.Token);
                        if (quiet == QuietRule.Outcome.Ran) result = ran;
                        else await Task.Delay(300, stop.Token);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
                if (result == true)
                {
                    removed++;
                    lock (organizeSync) if (orgPlan?.Steps.FirstOrDefault(s => s.Id == stepId) is { } step) { step.State = "skipped"; step.Note = "Removed again."; SavePlanLocked(); }
                }
                else kept++;
            }
            if (removed > 0) { await RebindOwned(); await RefreshAfterOrganize(); }
            OrganizeChanged();
            return new("ack", c.Id, true, Data: new { removed, kept });
        }
        finally { Volatile.Write(ref organizeBusy, 0); }
    }
}
