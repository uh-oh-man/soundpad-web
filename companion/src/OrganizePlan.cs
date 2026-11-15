using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace SoundpadWeb;

/// <summary>
/// One change in an organization plan (2.44). Native kinds are sent to Soundpad by Apply; manual kinds are guided
/// steps the host does in Soundpad, which the companion watches for (or the host ticks off). Paths are kept here
/// only: the website gets sound ids, category ids, names and states.
/// </summary>
public sealed class OrganizeStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>createCategory, addSound, removeCategory (native); removeFromCategory, renameCategory, createNested, reorder (manual).</summary>
    public string Kind { get; set; } = "";
    /// <summary>The new category's name (createCategory, createNested) or the new name (renameCategory).</summary>
    public string? Name { get; set; }
    public string? SoundPath { get; set; }
    public string? SoundTitle { get; set; }
    /// <summary>The category the step acts on (or, for removeFromCategory, the one the sound leaves).</summary>
    public CategoryRef? Target { get; set; }
    /// <summary>addSound into a category this plan creates first: that createCategory step.</summary>
    public string? TargetStepId { get; set; }
    /// <summary>pending, applying, verified, conflict, failed, skipped, manual, done or flagged.</summary>
    public string State { get; set; } = "pending";
    public string? Note { get; set; }
    /// <summary>For a conflict the host may resolve by confirming a candidate: that category, as it was found.</summary>
    public CategoryRef? Candidate { get; set; }
    public string By { get; set; } = "Host";
    /// <summary>"host", or the participant id of the guest whose proposal it came from.</summary>
    public string ByActor { get; set; } = "host";
    public string? ProposalId { get; set; }
    /// <summary>addSound, once verified: whether Soundpad's All sounds gained an entry for it.</summary>
    public bool? AddedToAllSounds { get; set; }
    /// <summary>createCategory, once verified: the category it made, so a partial apply can remove it again.</summary>
    public CategoryRef? Created { get; set; }
}
public sealed class OrganizeProposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string By { get; set; } = "Guest";
    public string ByActor { get; set; } = "";
    public long CreatedAt { get; set; }
    public List<OrganizeStep> Steps { get; set; } = [];
}
public sealed class OrganizeMeasured
{
    /// <summary>What the first add-to-category of this plan showed: unknown, noDuplicate or duplicates.</summary>
    public string AddBehaviour { get; set; } = "unknown";
}
/// <summary><c>organize\plan.v1.json</c> (2.44): the host's pending organization plan and guests' proposals. Older companions ignore it.</summary>
public sealed class OrganizePlan
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Revision { get; set; }
    /// <summary>draft, applying, paused or done.</summary>
    public string State { get; set; } = "draft";
    public string? BaseFingerprint { get; set; }
    /// <summary>Why it is paused: conflict, failed, duplicates, soundpad, restart, or null.</summary>
    public string? Paused { get; set; }
    public string? PausedText { get; set; }
    public List<OrganizeStep> Steps { get; set; } = [];
    public List<OrganizeProposal> Proposals { get; set; } = [];
    public OrganizeMeasured Measured { get; set; } = new();
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// The Organize files under <c>&lt;data&gt;\organize\</c> (2.44): the plan, its settings and checkpoints. Each is
/// strictly checked when read; a damaged plan is renamed aside and reported, never rewritten silently, and one
/// from a newer version is left alone (nothing is written over it). Writes go to a .tmp file, flushed, then moved.
/// </summary>
public sealed partial class OrganizeFiles(string dataDirectory)
{
    public const int MaxSteps = 200, MaxProposalSteps = 50, MaxProposalsPerGuest = 20, MaxNameLength = 80, MaxPlanBytes = 4 * 1024 * 1024, MaxSettingsBytes = 4096, KeptCheckpoints = 10;
    public static readonly string[] NativeKinds = ["createCategory", "addSound", "removeCategory"];
    public static readonly string[] ManualKinds = ["removeFromCategory", "renameCategory", "createNested", "reorder"];
    public static readonly string[] StepStates = ["pending", "applying", "verified", "conflict", "failed", "skipped", "manual", "done", "flagged"];
    public string Folder => Path.Combine(dataDirectory, "organize");
    public string PlanPath => Path.Combine(Folder, "plan.v1.json");
    public string SettingsPath => Path.Combine(Folder, "settings.v1.json");
    public string Checkpoints => Path.Combine(Folder, "checkpoints");
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    [GeneratedRegex("^[a-f0-9]{32}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex HashPattern();

    /// <summary>A name the app may send to Soundpad for a category: 1–80 printable characters Soundpad can receive, no quotes.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Type a name.";
        if (name.Length > MaxNameLength) return $"Use at most {MaxNameLength} characters.";
        if (name != name.Trim()) return "Remove the spaces at the start or end.";
        if (name.Any(c => char.IsControl(c) || c == '"' || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned)) return "Use letters, digits and ordinary punctuation, without quotation marks.";
        if (!SoundpadText.CanSend(name)) return "Soundpad can't receive some of these characters on this PC. Use plain letters.";
        return null;
    }

    public sealed record LoadResult(OrganizePlan? Plan, string? Problem, bool Newer);
    /// <summary>Reads the plan: none (no file), the plan, or why it could not be used (damaged: renamed aside; newer: left as it is).</summary>
    public LoadResult LoadPlan()
    {
        if (!File.Exists(PlanPath)) return new(null, null, false);
        byte[] bytes;
        try
        {
            if (new FileInfo(PlanPath).Length > MaxPlanBytes) return Corrupt("The organization plan is larger than 4 MiB.");
            bytes = File.ReadAllBytes(PlanPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(null, "The organization plan could not be read. It was left as it is.", true); }
        try
        {
            using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number) return Corrupt("The organization plan is damaged.");
                if (!version.TryGetInt32(out int v) || v != 1) return new(null, "The organization plan was written by a newer Soundpad Web. It was left as it is; update the companion to use it.", true);
            }
            var plan = JsonSerializer.Deserialize<OrganizePlan>(bytes, Strict) ?? throw new JsonException();
            Validate(plan);
            return new(plan, null, false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or ArgumentException) { return Corrupt("The organization plan is damaged."); }
    }
    private LoadResult Corrupt(string why)
    {
        var aside = Path.Combine(Folder, $"plan.v1.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
        try { File.Move(PlanPath, aside); return new(null, why + " It was kept as " + Path.GetFileName(aside) + " and a new plan starts.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(null, why + " It could not be moved aside, so it was left as it is.", true); }
    }
    public static void Validate(OrganizePlan plan)
    {
        if (plan.V != 1 || !IdPattern().IsMatch(plan.Id) || plan.Revision < 0 || plan.State is not ("draft" or "applying" or "paused" or "done")) throw new InvalidDataException("Invalid plan.");
        if (plan.Paused is not (null or "conflict" or "failed" or "duplicates" or "soundpad" or "restart") || plan.PausedText?.Length > 600 || plan.BaseFingerprint is { } f && !HashPattern().IsMatch(f)) throw new InvalidDataException("Invalid plan state.");
        if (plan.Measured.AddBehaviour is not ("unknown" or "noDuplicate" or "duplicates")) throw new InvalidDataException("Invalid measurement.");
        if (plan.Steps.Count > MaxSteps || plan.Proposals.Count > 200) throw new InvalidDataException("Too many steps.");
        var ids = new HashSet<string>();
        foreach (var step in plan.Steps) { ValidateStep(step); if (!ids.Add(step.Id)) throw new InvalidDataException("Duplicate step."); }
        foreach (var step in plan.Steps) if (step.TargetStepId != null && !plan.Steps.Any(s => s.Id == step.TargetStepId && s.Kind == "createCategory")) throw new InvalidDataException("A step targets an unknown step.");
        var proposals = new HashSet<string>();
        foreach (var proposal in plan.Proposals)
        {
            if (!IdPattern().IsMatch(proposal.Id) || !proposals.Add(proposal.Id) || proposal.By.Length is 0 or > 40 || proposal.ByActor.Length is 0 or > 128 || proposal.Steps.Count is 0 or > MaxProposalSteps) throw new InvalidDataException("Invalid proposal.");
            foreach (var step in proposal.Steps) { ValidateStep(step); if (step.TargetStepId != null && !proposal.Steps.Any(s => s.Id == step.TargetStepId && s.Kind == "createCategory")) throw new InvalidDataException("A proposal step targets an unknown step."); }
        }
        if (plan.Proposals.GroupBy(p => p.ByActor).Any(g => g.Count() > MaxProposalsPerGuest)) throw new InvalidDataException("Too many proposals.");
    }
    private static void ValidateStep(OrganizeStep s)
    {
        if (!IdPattern().IsMatch(s.Id) || !NativeKinds.Contains(s.Kind) && !ManualKinds.Contains(s.Kind) || !StepStates.Contains(s.State)) throw new InvalidDataException("Invalid step.");
        if (s.Name != null && NameProblem(s.Name) != null || s.SoundTitle?.Length > 4096 || s.Note?.Length > 600 || s.By.Length is 0 or > 40 || s.ByActor.Length is 0 or > 128 || s.ProposalId is { } p && !IdPattern().IsMatch(p)) throw new InvalidDataException("Invalid step fields.");
        if (s.SoundPath is { } path && (path.Length > 1024 || !Path.IsPathFullyQualified(path) || path.Any(c => c == '"' || char.IsControl(c)))) throw new InvalidDataException("Invalid step path.");
        foreach (var r in new[] { s.Target, s.Candidate, s.Created }) if (r != null && (r.Name.Length > 4096 || r.Chain.Length > 16 || r.Chain.Any(c => c.Length > 4096) || !HashPattern().IsMatch(r.PathsHash))) throw new InvalidDataException("Invalid category reference.");
        bool needsSound = s.Kind is "addSound" or "removeFromCategory", needsName = s.Kind is "createCategory" or "renameCategory" or "createNested", needsTarget = s.Kind is not ("createCategory" or "addSound");
        if (needsSound && s.SoundPath == null || needsName && s.Name == null || needsTarget && s.Target == null || s.Kind == "addSound" && (s.Target == null) == (s.TargetStepId == null)) throw new InvalidDataException("Incomplete step.");
    }
    public void SavePlan(OrganizePlan plan)
    {
        Validate(plan);
        Write(PlanPath, JsonSerializer.SerializeToUtf8Bytes(plan, Json.Options), MaxPlanBytes);
    }
    public void DeletePlan() { if (File.Exists(PlanPath)) File.Delete(PlanPath); }
    private void Write(string path, byte[] bytes, int limit)
    {
        if (bytes.Length > limit) throw new InvalidDataException("The organization data is too large.");
        Directory.CreateDirectory(Folder);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, true);
    }

    // ---- settings.v1.json: {format:1, guestOrganize, addBehaviour}; unknown fields are kept and written back ----
    public sealed record Settings(bool GuestOrganize, string AddBehaviour, bool Damaged);
    private JsonObject? settings;
    private bool settingsDamaged;
    private readonly object settingsSync = new();
    public Settings ReadSettings()
    {
        lock (settingsSync)
        {
            LoadSettings();
            return new(settings!["guestOrganize"]?.GetValue<bool>() ?? false, settings["addBehaviour"]?.GetValue<string>() ?? "unknown", settingsDamaged);
        }
    }
    private void LoadSettings()
    {
        if (settings != null) return;
        settings = Fresh(); settingsDamaged = false;
        if (!File.Exists(SettingsPath)) return;
        try
        {
            if (new FileInfo(SettingsPath).Length > MaxSettingsBytes) throw new InvalidDataException();
            if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is not JsonObject read || read["format"]?.GetValueKind() != JsonValueKind.Number || read["format"]!.GetValue<int>() != 1) throw new InvalidDataException();
            var guest = read["guestOrganize"]; var add = read["addBehaviour"];
            if (guest != null && guest.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException();
            if (add != null && (add.GetValueKind() != JsonValueKind.String || add.GetValue<string>() is not ("unknown" or "noDuplicate" or "duplicates"))) throw new InvalidDataException();
            foreach (var (name, value) in read.ToArray()) settings[name] = value?.DeepClone();
        }
        // A damaged file means the defaults (guests may not propose), and the file is left as it is until the host changes a setting.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException) { settings = Fresh(); settingsDamaged = true; }
    }
    private static JsonObject Fresh() => new() { ["format"] = 1, ["guestOrganize"] = false, ["addBehaviour"] = "unknown" };
    public Settings UpdateSettings(bool? guestOrganize = null, string? addBehaviour = null)
    {
        lock (settingsSync)
        {
            LoadSettings();
            var next = (JsonObject)settings!.DeepClone();
            if (guestOrganize is bool g) next["guestOrganize"] = g;
            if (addBehaviour != null) next["addBehaviour"] = addBehaviour is "unknown" or "noDuplicate" or "duplicates" ? addBehaviour : throw new InvalidDataException("Invalid setting.");
            next["format"] = 1;
            Write(SettingsPath, System.Text.Encoding.UTF8.GetBytes(next.ToJsonString()), MaxSettingsBytes);
            settings = next; settingsDamaged = false;
        }
        return ReadSettings();
    }

    // ---- checkpoints\<planId>.v1.json and <planId>.spl.bak ----
    /// <summary>
    /// Before Apply changes Soundpad: the whole tree (paths, titles, order) and a read-only copy of Soundpad's own
    /// soundlist.spl, written here (never into Soundpad's folder) under a name that is not .spl, so nothing reads it as
    /// a Soundpad file. Restoring it is the host's manual step, with Soundpad closed. Keeps the newest 10.
    /// </summary>
    public string? WriteCheckpoint(string planId, string soundlistXml, string categoriesXml, string? leppsoftFolder)
    {
        Directory.CreateDirectory(Checkpoints);
        var record = JsonSerializer.SerializeToUtf8Bytes(new { format = 1, planId, createdUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), soundlist = soundlistXml, categories = categoriesXml }, Json.Options);
        var path = Path.Combine(Checkpoints, planId + ".v1.json");
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(record); file.Flush(true); }
        File.Move(temporary, path, true);
        string? copy = null;
        if (leppsoftFolder != null)
        {
            var source = Path.Combine(leppsoftFolder, "soundlist.spl");
            if (File.Exists(source))
            {
                copy = Path.Combine(Checkpoints, planId + ".spl.bak");
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length > 64 * 1024 * 1024) copy = null;
                else { using var output = new FileStream(copy + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None); input.CopyTo(output); output.Flush(true); }
                if (copy != null) File.Move(copy + ".tmp", copy, true);
            }
        }
        // Only the app's own checkpoint names are ever deleted.
        var mine = Directory.EnumerateFiles(Checkpoints, "*.v1.json").Where(p => IdPattern().IsMatch(Path.GetFileName(p)[..^8])).Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeptCheckpoints).ToArray();
        foreach (var old in mine)
        {
            var id = old.Name[..^8];
            try { old.Delete(); var spl = Path.Combine(Checkpoints, id + ".spl.bak"); if (File.Exists(spl)) File.Delete(spl); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return copy;
    }
    public string? CheckpointCopy(string planId) { var p = Path.Combine(Checkpoints, planId + ".spl.bak"); return File.Exists(p) ? p : null; }
}
