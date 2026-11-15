using System.Text.Json;
using System.Text.Json.Serialization;
namespace SoundpadWeb;

// Organize mode (2.44, gate organize): the bodies of the organize ops, and the pure rules for steps (what Soundpad can
// do itself, what is a guided step, what each looks like to the website, and how a manual step is seen done).

/// <summary>One change asked for by the website. Sounds and categories by id only: never a path.</summary>
public sealed record OrganizeStepInput(
    string Kind,
    string? SoundId = null,
    // A category in the library the page shows (its id), with its name, so a stale id is refused.
    string? CategoryId = null, string? CategoryName = null,
    // A category this plan creates: an existing createCategory step, or one earlier in the same request (its key).
    string? TargetStepId = null, string? TargetKey = null,
    string? Name = null,
    // The page's own name for a createCategory in this request, so later inputs can add into it.
    string? Key = null,
    // The names of the category's parents as the page shows them (top first), so a stale id that now points at a
    // same-named category elsewhere is refused too.
    string[]? CategoryParents = null);
public sealed record OrganizeEditBody(long PlanRevision, OrganizeStepInput[]? Add = null, string[]? Remove = null, string? AcceptProposal = null, string? RejectProposal = null);
public sealed record OrganizeApplyBody(string PlanId, long PlanRevision);
public sealed record OrganizeResolveBody(string Resolution, string? StepId = null, string? TargetCategoryId = null, string? TargetCategoryName = null, string[]? TargetCategoryParents = null);
public sealed record OrganizeProposeBody(OrganizeStepInput[] Steps);
public sealed record OrganizeWithdrawBody(string ProposalId);
public sealed record OrganizeSettingsBody(bool GuestOrganize);
public sealed record OrganizeEmptyBody();

/// <summary>A category as the website sees it in a step: its id in the current library (null when it is gone), name and parents.</summary>
public sealed record OrganizeCategoryView(string? CategoryId, string Name, string[] Chain);
public sealed record OrganizeStepView(string Id, string Kind, string Support, string Reason, string State, string? Note, string By, bool Mine,
    string? SoundId, string? SoundName, OrganizeCategoryView? Target, string? TargetStepId, string? Name, OrganizeCategoryView? Candidate, string? ProposalId, bool? AddedToAllSounds);

internal static class OrganizeRules
{
    /// <summary>True when the page sent no parents (an older page) or the same parents the category has in Soundpad now.</summary>
    public static bool SameParents(string[]? parents, NativeNode node) => parents == null || parents.Length <= 16 && parents.SequenceEqual(node.Chain, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    /// <summary>The op's body, strictly: unknown fields, wrong types and a missing body are refused.</summary>
    public static T Body<T>(Command c) where T : class
    {
        if (c.Body is not { ValueKind: JsonValueKind.Object } body) throw new InvalidDataException("Invalid organize request.");
        if (body.GetRawText().Length > 256 * 1024) throw new InvalidDataException("The organize request is too large.");
        try { return body.Deserialize<T>(Strict) ?? throw new InvalidDataException("Invalid organize request."); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { throw new InvalidDataException("Invalid organize request."); }
    }
    public static bool IsNative(string kind) => kind is "createCategory" or "addSound" or "removeCategory";

    /// <summary>
    /// Whether Apply can do the step in Soundpad now ("native") or the host does it in Soundpad ("manual"), and why, in
    /// one line. A category removal is native only for an empty top-level category without subcategories.
    /// </summary>
    public static (string Support, string Reason) Classify(OrganizeStep step, NativeTree? tree)
    {
        string target = step.Target?.Name is { Length: > 0 } t ? "“" + t + "”" : "the category";
        string sound = step.SoundTitle is { Length: > 0 } s ? "“" + s + "”" : "the sound";
        switch (step.Kind)
        {
            case "createCategory": return ("native", "Soundpad can do this: a new top-level category at the end of the list.");
            case "addSound": return ("native", "Soundpad can do this: the sound is added at the end of the category.");
            case "removeCategory":
                var node = tree == null || step.Target == null ? null : tree.Find(step.Target, out _);
                if (node != null && !node.Root) return ("manual", $"Do this in Soundpad: Soundpad Web removes only top-level categories. Remove {target} there.");
                if (node != null && node.Children.Length > 0) return ("manual", $"Do this in Soundpad: remove the categories inside {target} first.");
                if (node != null && node.Paths.Length > 0) return ("manual", $"Do this in Soundpad first: move or remove the {node.Paths.Length} sound{(node.Paths.Length == 1 ? "" : "s")} in {target} (removing a category with sounds also takes them out of All sounds). Then Soundpad Web can remove it.");
                return ("native", "Soundpad can do this: the category is empty.");
            case "removeFromCategory": return ("manual", $"Do this in Soundpad: Soundpad can't take a sound out of a category from outside. Open {target} and remove {sound} from that category only.");
            case "renameCategory": return ("manual", $"Do this in Soundpad: rename {target} to “{step.Name}”. Soundpad can't rename from outside.");
            case "createNested": return ("manual", $"Do this in Soundpad: add “{step.Name}” inside {target}. Soundpad Web creates only top-level categories.");
            case "reorder": return ("manual", $"Do this in Soundpad: drag to reorder {target}. Soundpad can't reorder from outside. Mark it done when it's done.");
            default: return ("manual", "Do this in Soundpad.");
        }
    }

    /// <summary>
    /// A guided step seen done in Soundpad (re-checked on every library change): removeFromCategory when the sound has
    /// left the category (flagged when it left All sounds too), renameCategory and createNested when the category is
    /// there. Reorder is ticked off by the host. Returns true when the step changed.
    /// </summary>
    public static bool Watch(OrganizeStep step, NativeTree tree)
    {
        if (step.State is not ("manual" or "flagged")) return false;
        string before = step.State;
        switch (step.Kind)
        {
            case "removeFromCategory":
                var place = tree.Categories.Where(n => step.Target!.SamePlace(n)).ToArray();
                if (place.Length != 1 || step.SoundPath == null) return false;
                if (place[0].Paths.Contains(step.SoundPath, StringComparer.OrdinalIgnoreCase)) return false;
                if (tree.Count(step.SoundPath) == 0) { step.State = "flagged"; step.Note = "The sound left All sounds too. Add it to Soundpad again if that was not meant."; }
                else { step.State = "done"; step.Note = "Seen done in Soundpad."; }
                break;
            case "renameCategory":
                if (tree.Categories.Any(n => n.Name == step.Name && n.Chain.SequenceEqual(step.Target!.Chain)) && !tree.Categories.Any(step.Target!.SamePlace)) { step.State = "done"; step.Note = "Seen done in Soundpad."; }
                break;
            case "createNested":
                if (tree.Categories.Any(n => n.Name == step.Name && n.Chain.SequenceEqual(step.Target!.Chain.Append(step.Target.Name)))) { step.State = "done"; step.Note = "Seen done in Soundpad."; }
                break;
        }
        return step.State != before;
    }

    public static OrganizeCategoryView? CategoryView(CategoryRef? r, NativeTree? tree)
    {
        if (r == null) return null;
        string? id = null;
        if (tree != null)
        {
            var exact = tree.Categories.Where(r.Matches).ToArray();
            var place = exact.Length == 1 ? exact : tree.Categories.Where(r.SamePlace).ToArray();
            if (place.Length == 1) id = place[0].Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new(id, r.Name, r.Chain);
    }
    public static OrganizeStepView View(OrganizeStep s, NativeTree? tree, Func<string, string?> soundId, string? viewer)
    {
        var (support, reason) = Classify(s, tree);
        return new(s.Id, s.Kind, support, reason, s.State, s.Note, s.By, viewer != null && s.ByActor == viewer, s.SoundPath == null ? null : soundId(s.SoundPath), s.SoundTitle,
            CategoryView(s.Target, tree), s.TargetStepId, s.Name, CategoryView(s.Candidate, tree), s.ProposalId, s.AddedToAllSounds);
    }
}
