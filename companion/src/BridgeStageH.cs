namespace SoundpadWeb;

/// <summary>A backup taken before a risky step (2.44, WP1); Skipped with a reason when backups are not set up.</summary>
public record BackupCheckpoint(string Id, string Path, bool Skipped, string? Reason);

// Stage H (2.44) hook points, Phase 0. NOTE for the integrator: this is WP2's copy, made because the work packages were
// branched from 565ccab without Phase 0. It holds only what the updater needs; the other packages' members
// (ExecuteBackups/Organize/Setup, MissingRefusal, PublicLibraryH, the other snapshot values) join here at integration.
public sealed partial class Bridge
{
    /// <summary>The Stage H ops, in package order; the first non-null answer wins.</summary>
    private Task<Reply>? ExecuteStageH(Command c, string actor, Participant? p, string? requester, string? audience)
        => ExecuteUpdates(c, actor, p, requester, audience);

    /// <summary>After startup cleanup, or after an update resume (ResumeFromHandoff). Backups, Organize and Setup start here.</summary>
    private void StageHStarted() { }

    private void DisposeStageH()
    {
        try { UpdatesDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>A backup before a risky step (WP1 supplies it): until then, skipped with the reason.</summary>
    private Task<BackupCheckpoint?> CheckpointH(string kind, CancellationToken ct) => BackupCheckpointStub(kind, ct);
    private static Task<BackupCheckpoint?> BackupCheckpointStub(string kind, CancellationToken ct) => Task.FromResult<BackupCheckpoint?>(new("", "", true, "Backups are not set up."));

    private UpdateStatus? UpdateSnapshotH => UpdateSnapshot;
}
