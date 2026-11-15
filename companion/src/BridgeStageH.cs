namespace SoundpadWeb;

/// <summary>A backup taken before a risky step (2.44): its id and folder, or why none was taken.</summary>
public record BackupCheckpoint(string Id, string Path, bool Skipped, string? Reason);

/// <summary>
/// Stage H (2.44) wiring: backups, the updater, Organize mode, setup and host-wide hotkeys. Integrator-owned and
/// frozen; each work package fills in its own partial file (BridgeBackups.cs, BridgeUpdates.cs, BridgeOrganize.cs,
/// BridgeSetup.cs).
/// </summary>
public sealed partial class Bridge
{
    /// <summary>The first Stage H handler that knows the op answers it; null for every other op.</summary>
    private Task<Reply>? ExecuteStageH(Command c, string actor, Participant? p, string? requester, string? audience) =>
        ExecuteBackups(c, actor, p, requester, audience) ?? ExecuteUpdates(c, actor, p, requester, audience)
        ?? ExecuteOrganize(c, actor, p, requester, audience) ?? ExecuteSetup(c, actor, p, requester, audience);

    /// <summary>After startup cleanup (and, from 2.44, after an update resume).</summary>
    private void StageHStarted() { BackupsStarted(); OrganizeStarted(); SetupStarted(); }

    private void DisposeStageH()
    {
        try { BackupsDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { UpdatesDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { OrganizeDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { SetupDispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private Reply? MissingRefusal(Command c) => OrganizeMissingRefusal(c);
    private Library PublicLibraryH(Library l) => OrganizePublicLibrary(l);
    private Task<BackupCheckpoint?> CheckpointH(string kind, CancellationToken ct) => BackupCheckpoint(kind, ct);

    private long BackupsRevisionH => BackupsRevision;
    private UpdateStatus? UpdateSnapshotH => UpdateSnapshotNow;
    private long OrganizeRevisionH => OrganizeRevision;
    private string? SoundpadProblemH => SoundpadProblemNow;
    private bool? CompanionElevatedH => CompanionElevatedNow;
    private long HotkeysRevisionH => HotkeysRevision;
    private bool? SetupDoneH => SetupDoneNow;
}
