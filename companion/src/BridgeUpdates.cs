namespace SoundpadWeb;

/// <summary>The updater (2.44, WP2). Phase 0 stub: no op is handled and the snapshot carries no update state.</summary>
public sealed partial class Bridge
{
    private Task<Reply>? ExecuteUpdates(Command c, string actor, Participant? p, string? requester, string? audience) => null;
    private void UpdatesDispose() { }
    private UpdateStatus? UpdateSnapshotNow => null;
}
