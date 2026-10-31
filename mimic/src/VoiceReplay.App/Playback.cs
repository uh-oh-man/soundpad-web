using VoiceReplay.Core;
namespace VoiceReplay.App;

public sealed class Playback(Action<string>? log = null) : PreviewPlayer(log);
