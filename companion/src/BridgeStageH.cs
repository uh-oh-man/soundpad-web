namespace SoundpadWeb;

/// <summary>A backup made before a risky step (restore, update, organizing); <see cref="Skipped"/> with the reason when none could be made.</summary>
public sealed record BackupCheckpoint(string Id, string Path, bool Skipped, string? Reason);

// Stage H (2.44) wiring: each work package's commands and lifecycle, called from Bridge.cs and BridgeKeep.cs. This copy
// was made by WP1 (backups) on a base without Phase 0; it carries only the backups members. The integrator's Phase 0
// version (ExecuteBackups → ExecuteUpdates → ExecuteOrganize → ExecuteSetup, and the other hooks) replaces it.
public sealed partial class Bridge
{
    /// <summary>The Stage H commands; null when the op is not one of them (the first package that answers wins).</summary>
    private Task<Reply>? ExecuteStageH(Command c, string actor, Participant? p, string? requester, string? audience)
        => ExecuteBackups(c, actor, p, requester, audience);

    /// <summary>Once, when the companion has started (StartupCleanup).</summary>
    private void StageHStarted()
    {
        try { BackupsStarted(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log("Backups could not start: " + ex.Message); }
    }

    private void DisposeStageH()
    {
        try { BackupsDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>A backup before a risky step (kind "pre-restore", "pre-update" or "pre-organize").</summary>
    internal Task<BackupCheckpoint?> CheckpointH(string kind, CancellationToken ct) => BackupCheckpoint(kind, ct);

    private long BackupsRevisionH => BackupsRevision;
}
