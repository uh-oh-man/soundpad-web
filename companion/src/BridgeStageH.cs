namespace SoundpadWeb;

/// <summary>A backup made before a risky step (restore, update, organizing); <see cref="Skipped"/> with the reason when none could be made.</summary>
public sealed record BackupCheckpoint(string Id, string Path, bool Skipped, string? Reason);

// Stage H (2.44) wiring, called from Bridge.cs and BridgeKeep.cs: backups (BridgeBackups.cs), the updater
// (BridgeUpdates.cs) and Organize mode (BridgeOrganize.cs).
public sealed partial class Bridge
{
    /// <summary>The Stage H commands; null when the op is not one of them (the first package that answers wins).</summary>
    private Task<Reply>? ExecuteStageH(Command c, string actor, Participant? p, string? requester, string? audience)
        => ExecuteBackups(c, actor, p, requester, audience) ?? ExecuteUpdates(c, actor, p, requester, audience)
        ?? ExecuteOrganize(c, actor, p, requester, audience);

    private int stageHStarted;
    /// <summary>Once, when the companion has started (StartupCleanup), or after an update resume (ResumeFromHandoff).</summary>
    internal void StageHStarted()
    {
        if (Interlocked.Exchange(ref stageHStarted, 1) != 0) return;
        try { BackupsStarted(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log("Backups could not start: " + ex.Message); }
        try { OrganizeStarted(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log("Organize could not start: " + ex.Message); }
    }

    private void DisposeStageH()
    {
        try { BackupsDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { UpdatesDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { OrganizeDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>play, startAt and region of a sound whose file is missing are refused before anything else (2.44).</summary>
    private Reply? MissingRefusal(Command c) => OrganizeMissingRefusal(c);
    /// <summary>The library the website sees: missing files marked (2.44).</summary>
    private Library PublicLibraryH(Library l) => OrganizePublicLibrary(l);
    /// <summary>After every successful library refresh: missing-file scans and guided steps follow library changes.</summary>
    private void LibraryRefreshedH() => OrganizeLibraryRefreshed();

    /// <summary>A backup before a risky step (kind "pre-restore", "pre-update" or "pre-organize").</summary>
    internal Task<BackupCheckpoint?> CheckpointH(string kind, CancellationToken ct) => BackupCheckpoint(kind, ct);

    private long BackupsRevisionH => BackupsRevision;
    private UpdateStatus? UpdateSnapshotH => UpdateSnapshot;
    private long OrganizeRevisionH => OrganizeRevisionNow;
}
