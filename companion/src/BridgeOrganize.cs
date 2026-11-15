namespace SoundpadWeb;

/// <summary>Organize mode and missing files (2.44, WP3). Phase 0 stub: no op is handled and the library is unchanged.</summary>
public sealed partial class Bridge
{
    private Task<Reply>? ExecuteOrganize(Command c, string actor, Participant? p, string? requester, string? audience) => null;
    private void OrganizeStarted() { }
    private void OrganizeDispose() { }
    private long OrganizeRevision => 0;
    private Reply? OrganizeMissingRefusal(Command c) => null;
    private Library OrganizePublicLibrary(Library l) => l;
}
