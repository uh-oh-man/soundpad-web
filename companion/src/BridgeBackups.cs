namespace SoundpadWeb;

/// <summary>Backups and restore (2.44, WP1). Phase 0 stub: no op is handled and no backup is ever taken.</summary>
public sealed partial class Bridge
{
    private Task<Reply>? ExecuteBackups(Command c, string actor, Participant? p, string? requester, string? audience) => null;
    private void BackupsStarted() { }
    private void BackupsDispose() { }
    private long BackupsRevision => 0;
    private Task<BackupCheckpoint?> BackupCheckpoint(string kind, CancellationToken ct) => Task.FromResult<BackupCheckpoint?>(new("", "", Skipped: true, Reason: "Backups are not set up."));
}
