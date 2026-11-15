namespace SoundpadWeb;

// Stage H (2.44) hook points (spec section 3.2). Phase 0 owns this file; this is the WP3 subset (Organize only),
// written to the Phase 0 names so the integrator can replace it with the full version (backups, updates, setup).
public sealed partial class Bridge
{
    /// <summary>The Stage H ops: the first handler that knows the op answers it; null for every other op.</summary>
    private Task<Reply>? ExecuteStageH(Command c, string actor, Participant? p, string? requester, string? audience) =>
        ExecuteOrganize(c, actor, p, requester, audience);
    private int stageHStarted;
    /// <summary>After the startup cleanup (and, with the updater, after a resume): starts each Stage H service once.</summary>
    internal void StageHStarted()
    {
        if (Interlocked.Exchange(ref stageHStarted, 1) != 0) return;
        try { OrganizeStarted(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log("Organize could not start: " + ex.Message); }
    }
    private void DisposeStageH()
    {
        try { OrganizeDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    /// <summary>play, startAt and region of a sound whose file is missing are refused before anything else (2.44).</summary>
    private Reply? MissingRefusal(Command c) => OrganizeMissingRefusal(c);
    /// <summary>The library the website sees: missing files marked (2.44).</summary>
    private Library PublicLibraryH(Library l) => OrganizePublicLibrary(l);
    private long OrganizeRevisionH => OrganizeRevisionNow;
    /// <summary>After every successful library refresh (WP3 addition): missing-file scans and guided steps follow library changes.</summary>
    private void LibraryRefreshedH() => OrganizeLibraryRefreshed();
}
