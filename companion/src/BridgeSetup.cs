namespace SoundpadWeb;

/// <summary>Setup, Soundpad diagnosis and host-wide hotkeys (2.44, WP4). Phase 0 stub: no op is handled.</summary>
public sealed partial class Bridge
{
    private Task<Reply>? ExecuteSetup(Command c, string actor, Participant? p, string? requester, string? audience) => null;
    private void SetupStarted() { }
    private void SetupDispose() { }
    private string? SoundpadProblemNow => null;
    private bool? CompanionElevatedNow => null;
    private long HotkeysRevision => 0;
    private bool? SetupDoneNow => null;
}
