using System.Diagnostics;
using System.Text.Json;
namespace SoundpadWeb.Testing;

/// <summary>
/// "--real-responsiveness-probe [--seconds S] [--seed N] [--out file.json] --data-dir &lt;folder&gt;" (2.44 real-hardware
/// proof): the 2.41 claim that Play, Stop and Panic stay prompt while slow audio work runs, on the REAL Soundpad.
/// A real Bridge on a throwaway data folder (never the companion's) drives Soundpad through its pipe while keep-pitch
/// copies "and play" and keep-pitch previews of a 40 s Temp sound take turns every ~1.2 s, Mimic records the harness's
/// fixture source (no audio device) with recognition keeping a core busy (300 ms per second of speech), and the host
/// presses Panic (and lifts it) every 2-3 s. Meanwhile a host and a guest click Play, cue starts, regions and Stop on
/// three Temp sounds. Every sound is a quiet tone (about -48 dBFS) with its own frequency, and Soundpad's own output is
/// captured (process loopback, an output stream; never a microphone), so the end shows what Soundpad really played
/// last. Answer times and obsolete plays are measured as in --playback-bench, with the time the Bridge handed a
/// DoPlaySound to the pipe standing in for the mock's arrival time. At the end it stops, waits until Soundpad has
/// stayed stopped for 2 s, ends the session (the Temp category goes with its slots and copies) and checks that
/// Soundpad's list is exactly as before. Refuses while the companion or VRChat runs or Soundpad is busy.
/// </summary>
internal static class RealResponsivenessProbe
{
    /// <summary>Notes when the Bridge handed each play or stop to the pipe and when Soundpad answered.</summary>
    private sealed class TimedPipe(SoundpadPipe inner) : ISoundpad, IAtomicOwnedEntryRemoval
    {
        public readonly List<(long Issued, long Returned, string Command)> Calls = [];
        public bool CanRemoveOwnedAtomically => inner.CanRemoveOwnedAtomically;
        public Task<bool> RemoveOwnedEntry(string path, string expectedCatalogFingerprint) => inner.RemoveOwnedEntry(path, expectedCatalogFingerprint);
        public async Task<string> Request(string command, CancellationToken cancel = default)
        {
            long issued = Stopwatch.GetTimestamp();
            try { return await inner.Request(command, cancel); }
            finally { if (command.StartsWith("DoPlaySound(") || command == "DoStopSound()") lock (Calls) Calls.Add((issued, Stopwatch.GetTimestamp(), command)); }
        }
    }
    /// <summary>The fixture's phrases after keeping a core busy as long as Whisper would (as --playback-bench --slow-work).</summary>
    private sealed class BusyTranscripts(int burnMsPerSecond) : ITranscriptEngine
    {
        private readonly TranscriptFixture inner = new();
        public int Calls => inner.Calls;
        public bool Available => inner.Available;
        public string Status => inner.Status;
        public void UseMode(SubtitleMode mode) => inner.UseMode(mode);
        public Task Prepare(CancellationToken cancel) => inner.Prepare(cancel);
        public Task<IReadOnlyList<TranscribedSpan>> Transcribe(float[] samples16k, CancellationToken cancel)
        {
            long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * burnMsPerSecond * samples16k.Length / MimicTranscriber.SpeechRate / 1000;
            double sink = 0; while (Stopwatch.GetTimestamp() < until && !cancel.IsCancellationRequested) sink += Math.Sqrt(sink + 1);
            return inner.Transcribe(samples16k, cancel);
        }
        public void Dispose() => inner.Dispose();
    }
    private sealed class Sent
    {
        public int Seq; public string Client = "", Op = "", Kind = ""; public string? SoundId; public int? Index; public bool Intent;
        public long Intended, Arrived, Acked; public Reply? Reply; public string? Failure; public Task? Task;
        public long? Reached, ReachedReturned; public long? NewerArrived;
        public bool Superseded => Flag("superseded") == true || Flag("played") == false;
        private bool? Flag(string name)
        {
            if (Reply is not { Ok: true, Data: not null }) return null;
            using var data = JsonDocument.Parse(JsonSerializer.Serialize(Reply.Data, Json.Options));
            return data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        }
    }
    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
    private static string Dist(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return "n=0";
        double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return $"n={sorted.Length} p50={At(0.5):0.0} p90={At(0.9):0.0} p99={At(0.99):0.0} max={sorted[^1]:0.0} ms";
    }
    private const int Rate = 48000;
    private static readonly double[] TargetHz = [600, 900, 1200];
    private const double SlowHz = 300;

    public static async Task<int> Run(string[] args, string root)
    {
        string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int seconds = int.TryParse(Arg("--seconds"), out var s) && s is >= 5 and <= 120 ? s : 20;
        int seed = int.TryParse(Arg("--seed"), out var chosen) ? chosen : 1;
        const double grace = 3;
        var output = Arg("--out");
        var log = Console.Out;
        using var inner = new SoundpadPipe();
        if (await RealProbes.Refusal(inner) is string refused) { log.WriteLine("ABORT: " + refused); return 2; }
        int soundpad = Process.GetProcessesByName("Soundpad").First().Id;
        HostHotkeys.ForbidWindowsHotkeys = true;
        var tag = Guid.NewGuid().ToString("N")[..8];
        // Short names: Soundpad refuses a path of 260 characters or more, and --data-dir can be deep.
        var folder = Path.Combine(root, "r" + tag[..4]);
        Directory.CreateDirectory(folder); folder = SlotFiles.LongPath(folder);
        var before = await ManagedSoundpad.Read(inner);
        log.WriteLine($"before: {before.Entries.Length} sounds, {before.Categories.Length} categories");
        var pipe = new TimedPipe(inner);
        Bridge? bridge = null; WasapiMimicCapture? capture = null; int result = 2;
        async Task<NativeCatalog> Settle(Func<NativeCatalog, bool> done) { var c = await ManagedSoundpad.Read(inner); for (int i = 0; i < 60 && !done(c); i++) { await Task.Delay(150); c = await ManagedSoundpad.Read(inner); } return c; }
        try
        {
            var live = new Bridge(pipe, new StateStore(folder));
            bridge = live;
            live.MimicLog += message => log.WriteLine("  companion log: " + message);
            var busy = new BusyTranscripts(300);
            live.UseMimicCapture(new MimicFixture()); live.UseTranscriptEngine(busy);
            live.UseHotkeys(key => new HotkeyFixture(key)); live.UseVoiceReplayLibrary(VoiceReplayFixture.Create(Path.Combine(folder, "voice-replay")));
            await live.Poll(true);
            const string audience = "probe-audience", guestPeer = "probe-guest-peer";
            var hostPerson = new Participant(Guid.NewGuid().ToString(), "Probe Host"); var guestPerson = new Participant(Guid.NewGuid().ToString(), "Probe Guest");
            Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
            var begin = await live.Execute(Cmd("beginSession") with { AudienceId = audience }, "host");
            if (!begin.Ok) { log.WriteLine("ABORT: beginSession: " + begin.Error); return 2; }
            static string? AssetId(Reply reply) { if (reply.Data == null) return null; using var data = JsonDocument.Parse(JsonSerializer.Serialize(reply.Data, Json.Options)); return data.RootElement.TryGetProperty("assetId", out var id) ? id.GetString() : null; }
            async Task<string> Upload(double hz, int ms, string name)
            {
                var wav = MimicRing.Wav(ExactPlaybackAnalysis.Tones(Rate, ExactPlaybackAnalysis.QuietLevel, (hz, ms)), Rate);
                var reply = await live.Upload(wav, name, "Probe Host", true, "host", audience);
                if (!reply.Ok || AssetId(reply) is not string id) throw new InvalidOperationException("upload " + name + ": " + reply.Error);
                return id;
            }
            // Three 3 s targets (600, 900, 1200 Hz) and the 40 s source of the slow work (300 Hz), all about -48 dBFS.
            var targetAssets = new List<string>();
            for (int i = 0; i < TargetHz.Length; i++) targetAssets.Add(await Upload(TargetHz[i], 3000, $"T{TargetHz[i]:0}.wav"));
            var slowAsset = await Upload(SlowHz, 40000, "S.wav");
            log.WriteLine("uploaded three 3 s targets and the 40 s slow source");
            var listening = await live.Execute(Cmd("mimicStart") with { SourceId = MimicFixture.Source.Id }, "host");
            var words = await live.Execute(Cmd("mimicTranscribe") with { Action = "enable" }, "host");
            log.WriteLine($"Mimic on the fixture source: {listening.Ok} {listening.Error}; recognition: {words.Ok} {words.Error}");
            await live.Poll(true);
            var targets = targetAssets.Select(a => live.Snapshot.Library.Sounds.First(x => x.AssetId == a && !x.InternalVariant)).ToArray();
            // Soundpad index of each target: the catalog entry whose path the Bridge maps to that sound id.
            var catalog = await ManagedSoundpad.Read(inner);
            var indexOf = new Dictionary<string, int>();
            foreach (var entry in catalog.Entries) if (live.SoundIdOf(entry.Path) is string id && targets.Any(t => t.Id == id)) indexOf[id] = entry.Index;
            if (indexOf.Count != targets.Length) { log.WriteLine("ABORT: the targets are not all listed in Soundpad"); return 2; }
            var hzOfIndex = targets.Select((t, i) => (indexOf[t.Id], TargetHz[i])).ToDictionary(p => p.Item1, p => p.Item2);

            // Warm-up, as in the bench: one cue start, a Stop and a quiet moment, so the slots get registered.
            await live.Execute(Cmd("startAt") with { SoundId = targets[0].Id, PositionMs = 500, LibraryVersion = live.Snapshot.Library.Version }, "host");
            await Task.Delay(300);
            await live.Execute(Cmd("stop"), "host");
            for (int i = 0; i < 40 && !live.SlotsReady; i++) { await Task.Delay(150); await live.Poll(i % 6 == 0); }
            await live.Poll(true);
            log.WriteLine("slots registered: " + live.SlotsReady);
            var slotIndexes = (await ManagedSoundpad.Read(inner)).Entries.Where(e => SlotPool.IsSlotFile(e.Path)).Select(e => e.Index).ToHashSet();

            capture = new WasapiMimicCapture();
            var ring = new MimicRing(capture.Rate, 180);
            await capture.Start(new MimicSource("app:" + soundpad, "Soundpad", "app"), ring);
            log.WriteLine("capture of Soundpad's output: " + capture.Status);
            capture.Pump(); await Task.Delay(400); capture.Pump();
            long idleAt = ring.Written; await Task.Delay(300); capture.Pump();
            double floor = ExactPlaybackAnalysis.Floor(ring.Read(idleAt, ring.Written));
            // Ring frame of a Stopwatch timestamp: the capture is pumped to "now" right after each mark.
            var marks = new List<(long Tick, long Frame)>();
            void Mark() { capture.Pump(); lock (marks) marks.Add((Stopwatch.GetTimestamp(), ring.Written)); }
            long FrameAt(long tick) { lock (marks) { var m = marks.OrderBy(x => Math.Abs(x.Tick - tick)).First(); return m.Frame + (long)((tick - m.Tick) * (double)ring.Rate / Stopwatch.Frequency); } }
            lock (pipe.Calls) pipe.Calls.Clear();

            var sent = new List<Sent>(); var order = new object(); int sequence = 0;
            Sent Send(string client, Command command, string kind, bool intent, string actor, Participant? person, string? requester)
            {
                var item = new Sent { Client = client, Op = command.Op, Kind = kind, SoundId = command.SoundId, Intent = intent, Intended = Stopwatch.GetTimestamp() };
                if (command.SoundId != null && indexOf.TryGetValue(command.SoundId, out int index)) item.Index = index;
                lock (order)
                {
                    item.Seq = ++sequence; sent.Add(item);
                    Task<Reply> running;
                    try { running = live.Execute(command with { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, actor, person, requester, actor == "host" ? null : audience); }
                    catch (Exception ex) { running = Task.FromException<Reply>(ex); }
                    item.Arrived = Stopwatch.GetTimestamp();
                    item.Task = running.ContinueWith(t =>
                    {
                        item.Acked = Stopwatch.GetTimestamp();
                        if (t.IsCompletedSuccessfully) item.Reply = t.Result; else item.Failure = "exception: " + (t.Exception?.InnerException?.GetType().Name ?? "cancelled");
                    }, TaskScheduler.Default);
                }
                return item;
            }
            using var stop = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            async Task Client(string name, string actor, Participant person, string? requester, int clientSeed)
            {
                var random = new Random(clientSeed);
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    await Task.Delay(random.Next(60, 260));
                    int roll = random.Next(100); var target = targets[random.Next(targets.Length)];
                    var command = Cmd(roll < 50 ? "play" : roll < 70 ? "startAt" : roll < 85 ? "region" : "stop") with { SoundId = target.Id, LibraryVersion = live.Snapshot.Library.Version };
                    string kind = "temp";
                    if (command.Op == "stop") { command = command with { SoundId = null, LibraryVersion = null }; kind = "stop"; }
                    else if (command.Op == "startAt") command = command with { PositionMs = random.Next(0, 2000) };
                    else if (command.Op == "region") { long from = random.Next(0, 2000); command = command with { PositionMs = from, EndMs = from + random.Next(200, 900) }; }
                    Send(name, command, kind, true, actor, person, requester);
                }
            }
            int polls = 0, pollFailures = 0;
            async Task PollLoop()
            {
                long next = 0;
                while (!stop.IsCancellationRequested)
                {
                    bool refresh = clock.ElapsedMilliseconds >= next; if (refresh) next = clock.ElapsedMilliseconds + 2000;
                    try { await live.Poll(refresh); polls++; } catch (Exception) { pollFailures++; }
                    Mark();
                    try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { }
                }
            }
            // Three kinds in turn: a keep-pitch copy "and play" of the whole 40 s (the slowest work), a keep-pitch preview,
            // and an echo copy "and play". A keep-pitch copy's file name ("S (0.6x speed, same pitch)-normal-....wav")
            // makes its path too long for Soundpad under the probe's deep --data-dir, so its import can fail there.
            Command Slow(int round) => round % 3 == 2
                ? Cmd("trimAudio") with { AssetId = slowAsset, PositionMs = 0, EndMs = 40000, Action = "echo", Autoplay = true }
                : Cmd(round % 3 == 0 ? "trimAudio" : "trimPreview") with { AssetId = slowAsset, PositionMs = 0, EndMs = 40000, Action = "speed", Speed = round % 3 == 0 ? 0.6 : 1.4, KeepPitch = true, Autoplay = round % 3 == 0 };
            async Task SlowWork()
            {
                for (int round = 0; clock.Elapsed.TotalSeconds < seconds - 2; round++)
                {
                    var copy = Slow(round);
                    Send("slow", copy, copy.Op == "trimAudio" ? "slow-copy" : "slow-preview", copy.Autoplay, "host", hostPerson, null);
                    try { await Task.Delay(1200, stop.Token); } catch (OperationCanceledException) { }
                }
            }
            async Task Panics()
            {
                var random = new Random(seed + 200);
                while (clock.Elapsed.TotalSeconds < seconds - 1)
                {
                    await Task.Delay(random.Next(2000, 3000));
                    Send("host", Cmd("panic") with { Action = "enable" }, "panic", true, "host", hostPerson, null);
                    await Task.Delay(100);
                    Send("host", Cmd("panic") with { Action = "disable" }, "panic-lift", false, "host", hostPerson, null);
                }
            }
            Mark();
            var poller = PollLoop();
            await Task.WhenAll(Client("host", "host", hostPerson, null, seed), Client("guest", "guest", guestPerson, guestPeer, seed + 1), SlowWork(), Panics());
            log.WriteLine($"clicking done after {clock.Elapsed.TotalSeconds:0.0} s");

            // The final burst while a slow copy "and play" is still being made: two plays, then a cue start (a slot).
            // Soundpad must end up playing only the cue start's tone, and the overtaken copy must never play.
            var finalCopy = Send("slow", Slow(2), "slow-copy", true, "host", hostPerson, null);
            await Task.Delay(20); // well inside the copy's ~120-150 ms of work on this PC
            Send("host", Cmd("play") with { SoundId = targets[0].Id, LibraryVersion = live.Snapshot.Library.Version }, "temp", true, "host", hostPerson, null);
            await Task.Delay(30);
            Send("guest", Cmd("play") with { SoundId = targets[1].Id, LibraryVersion = live.Snapshot.Library.Version }, "temp", true, "guest", guestPerson, guestPeer);
            await Task.Delay(30);
            var lastIntent = Send("host", Cmd("startAt") with { SoundId = targets[2].Id, PositionMs = 1000, LibraryVersion = live.Snapshot.Library.Version }, "temp", true, "host", hostPerson, null);
            long finalAt = lastIntent.Intended;
            Task[] pending; lock (order) pending = sent.Select(x => x.Task!).ToArray();
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(60)); } catch (TimeoutException) { }
            await Task.Delay(1500); Mark();
            string statusAfter = await inner.Request("GetPlayStatus()");
            // From 250 ms after the newest intent: sound of the plays just before it can still be on its way out then.
            var tail = ring.Read(FrameAt(finalAt) + ring.Rate / 4, ring.Written);
            var heard = TargetHz.Append(SlowHz).ToDictionary(hz => hz, hz => ExactPlaybackAnalysis.Span(tail, ring.Rate, hz, floor));
            // The last 2 s of the cue start's tone (3 s sound from 1.0 s): it should be the only thing after the burst.
            bool lastRight = heard[TargetHz[2]] != null && heard[SlowHz] == null && heard[TargetHz[0]] == null && heard[TargetHz[1]] == null && finalCopy.Reply is { Ok: true } && finalCopy.Superseded;
            log.WriteLine($"FINAL burst: copy-and-play answered {(finalCopy.Reply == null ? "never" : finalCopy.Superseded ? "made, not played (superseded)" : finalCopy.Reply.Ok ? "made and played" : "error " + finalCopy.Reply.Error)} {Ms(finalCopy.Intended, finalCopy.Acked):0} ms after it was sent; Soundpad status after: {statusAfter}");
            log.WriteLine($"FINAL heard after the last intent: " + string.Join(", ", heard.Select(p => $"{p.Key:0} Hz {(p.Value == null ? "none" : $"{p.Value.Value.Ms} ms from {(p.Value.Value.First * 1000.0 / ring.Rate + 250):0} ms")}")) + $" -> {(lastRight ? "ONLY the newest (the cue start)" : "NOT only the newest")}");

            // Stop at the end, timed in the capture: play the 900 Hz target, Stop after 600 ms.
            await Task.Delay(400);
            var lastPlay = Send("host", Cmd("play") with { SoundId = targets[1].Id, LibraryVersion = live.Snapshot.Library.Version }, "temp", true, "host", hostPerson, null);
            await lastPlay.Task!; await Task.Delay(600);
            var finalStop = Send("host", Cmd("stop"), "stop", true, "host", hostPerson, null);
            await finalStop.Task!; await Task.Delay(800); Mark();
            var stopData = ring.Read(FrameAt(lastPlay.Intended), ring.Written);
            var tone = ExactPlaybackAnalysis.Span(stopData, ring.Rate, TargetHz[1], floor);
            double stopOffsetMs = Ms(lastPlay.Intended, finalStop.Intended);
            log.WriteLine($"FINAL Stop: answered in {Ms(finalStop.Intended, finalStop.Acked):0.0} ms; the tone ended {(tone == null ? "?" : $"{tone.Value.Last * 1000.0 / ring.Rate - stopOffsetMs:0} ms")} after Stop was sent (capture latency included); status {await inner.Request("GetPlayStatus()")}");
            stop.Cancel(); await poller;
            capture.Stop();

            (long Issued, long Returned, string Command)[] calls; lock (pipe.Calls) calls = pipe.Calls.ToArray();
            Sent[] commands; lock (order) commands = sent.ToArray();
            var intents = commands.Where(x => x.Intent).OrderBy(x => x.Seq).ToArray();
            // A guest's click while Panic is on is refused at once (guest playback is paused), so it is no newer intent:
            // the host's own earlier click still stands. Only clicks that can win count as "newer".
            var panicWindows = commands.Where(x => x.Kind == "panic").Select(x => (From: x.Arrived, To: commands.Where(y => y.Kind == "panic-lift" && y.Seq > x.Seq).Select(y => y.Arrived).DefaultIfEmpty(long.MaxValue).First())).ToArray();
            bool CanWin(Sent x) => !(x.Client == "guest" && panicWindows.Any(w => x.Arrived >= w.From && x.Arrived < w.To));
            for (int i = 0; i < intents.Length; i++) intents[i].NewerArrived = intents.Skip(i + 1).FirstOrDefault(CanWin)?.Arrived;
            int unattributed = 0; var claimed = new HashSet<Sent>();
            foreach (var call in calls.Where(c => c.Command.StartsWith("DoPlaySound(")).OrderBy(c => c.Issued))
            {
                int index = int.Parse(call.Command[12..^1]); bool slot = slotIndexes.Contains(index);
                var owner = intents.Where(x => !claimed.Contains(x) && x.Op is not ("stop" or "panic") && x.Intended <= call.Issued && (x.Acked == 0 || x.Acked >= call.Returned) && (slot ? x.Op is "startAt" or "region" : x.Index == index)).OrderBy(x => x.Acked == 0 ? long.MaxValue : x.Acked).FirstOrDefault()
                    ?? intents.Where(x => !claimed.Contains(x) && x.Op == "trimAudio" && x.Intended <= call.Issued && (x.Acked == 0 || x.Acked >= call.Returned)).OrderBy(x => x.Acked == 0 ? long.MaxValue : x.Acked).FirstOrDefault();
                if (owner == null) { unattributed++; continue; }
                claimed.Add(owner); owner.Reached = call.Issued; owner.ReachedReturned = call.Returned;
            }
            var started = intents.Where(x => x.Reached != null).ToArray();
            double Staleness(Sent x) => x.NewerArrived is long newer ? Ms(newer, x.Reached!.Value) : double.NegativeInfinity;
            double StalenessAtAnswer(Sent x) => x.NewerArrived is long newer ? Ms(newer, x.ReachedReturned!.Value) : double.NegativeInfinity;
            var obsolete = started.Where(x => Staleness(x) >= grace).ToArray();
            var overtaken = started.Where(x => Staleness(x) < grace && StalenessAtAnswer(x) > 0).ToArray();
            var stops = intents.Where(x => x.Op == "stop" && x.Acked != 0).ToArray();
            var panics = intents.Where(x => x.Op == "panic" && x.Acked != 0).ToArray();
            var slow = commands.Where(x => x.Kind is "slow-copy" or "slow-preview").ToArray();
            var errors = intents.Where(x => x.Reply is { Ok: false } || x.Failure != null).GroupBy(x => x.Op + ": " + (x.Failure ?? x.Reply!.Error ?? "(no message)")).ToDictionary(g => g.Key, g => g.Count());
            var copyPlays = started.Where(x => x.Op == "trimAudio").ToArray();
            log.WriteLine($"Real Soundpad, {seconds} s, seed {seed}; companion {live.Snapshot.CompanionVersion}");
            log.WriteLine($"Playback commands: {intents.Length} ({string.Join(", ", intents.GroupBy(x => x.Op).Select(g => g.Count() + " " + g.Key))}); answers: {intents.Count(x => x.Reply is { Ok: true } && !x.Superseded)} ok, {intents.Count(x => x.Superseded)} superseded, {errors.Values.Sum()} errors, {intents.Count(x => x.Acked == 0)} unanswered");
            foreach (var (message, count) in errors) log.WriteLine($"  error x{count}: {message}");
            log.WriteLine("Send -> answer (Stop): " + Dist(stops.Select(x => Ms(x.Intended, x.Acked))));
            log.WriteLine("Send -> answer (Panic): " + Dist(panics.Select(x => Ms(x.Intended, x.Acked))));
            log.WriteLine("Send -> taken by the caller (Stop): " + Dist(stops.Select(x => Ms(x.Intended, x.Arrived))));
            log.WriteLine("Send -> taken by the caller (Panic): " + Dist(panics.Select(x => Ms(x.Intended, x.Arrived))));
            log.WriteLine("Send -> taken by the caller (plays, cue starts, regions): " + Dist(intents.Where(x => x.Op is "play" or "startAt" or "region").Select(x => Ms(x.Intended, x.Arrived))));
            log.WriteLine("Send -> DoPlaySound handed to the pipe (plays that started): " + Dist(started.Select(x => Ms(x.Intended, x.Reached!.Value))));
            log.WriteLine("Send -> answer (plays, cue starts, regions): " + Dist(intents.Where(x => x.Op is "play" or "startAt" or "region" && x.Acked != 0).Select(x => Ms(x.Intended, x.Acked))));
            log.WriteLine($"DoPlaySound calls: {calls.Count(c => c.Command.StartsWith("DoPlaySound("))} ({calls.Count(c => c.Command.StartsWith("DoPlaySound(") && slotIndexes.Contains(int.Parse(c.Command[12..^1])))} slot plays), DoStopSound calls: {calls.Count(c => c.Command == "DoStopSound()")}; obsolete (a newer intent had arrived {grace}+ ms before the play was handed to the pipe): {obsolete.Length}{(obsolete.Length > 0 ? $", worst {obsolete.Max(Staleness):0.0} ms" : "")}; overtaken while on the wire: {overtaken.Length}; unmatched: {unattributed}");
            log.WriteLine($"Copies \"and play\" that played: {copyPlays.Length} (of {slow.Count(x => x.Op == "trimAudio")} sent), of which obsolete: {copyPlays.Count(x => Staleness(x) >= grace)}");
            log.WriteLine($"Slow work: {string.Join(", ", slow.GroupBy(x => x.Kind).Select(g => g.Count() + " " + g.Key))}; answers: {string.Join(", ", slow.GroupBy(x => x.Failure ?? (x.Reply == null ? "unanswered" : !x.Reply.Ok ? "error: " + x.Reply.Error : x.Superseded ? "made, not played" : "made")).Select(g => g.Count() + " " + g.Key))}; recognitions {busy.Calls}");
            log.WriteLine("Slow work: caller held by each: " + Dist(slow.Select(x => Ms(x.Intended, x.Arrived))));
            log.WriteLine("Slow work: send -> answer: " + Dist(slow.Where(x => x.Acked != 0).Select(x => Ms(x.Intended, x.Acked))));
            log.WriteLine($"Polls: {polls} ({pollFailures} failed)");
            double stopMax = stops.Length == 0 ? 0 : stops.Max(x => Ms(x.Intended, x.Acked)), panicMax = panics.Length == 0 ? 0 : panics.Max(x => Ms(x.Intended, x.Acked));
            // Errors of the playback controls fail the run; the slow copies' answers are reported above (see Slow above).
            bool controlErrors = intents.Any(x => x.Op is "play" or "startAt" or "region" or "stop" or "panic" && (x.Reply is { Ok: false } || x.Failure != null));
            bool pass = obsolete.Length == 0 && lastRight && stopMax < 250 && panicMax < 250 && !controlErrors && intents.All(x => x.Acked != 0);
            log.WriteLine($"RESULT: Stop max {stopMax:0.0} ms, Panic max {panicMax:0.0} ms, obsolete plays {obsolete.Length}, last thing played right: {lastRight} -> {(pass ? "PASS" : "FAIL")} (limits: Stop and Panic under 250 ms, no obsolete play, no error from Play, cue starts, regions, Stop or Panic)");
            if (output != null)
            {
                var detail = commands.Select(x => new { seq = x.Seq, client = x.Client, op = x.Op, kind = x.Kind, index = x.Index, sentMs = Math.Round(Ms(commands[0].Intended, x.Intended), 1), takenMs = Math.Round(Ms(x.Intended, x.Arrived), 1), ackMs = x.Acked == 0 ? (double?)null : Math.Round(Ms(x.Intended, x.Acked), 1), ack = x.Failure ?? (x.Reply == null ? "unanswered" : !x.Reply.Ok ? "error: " + x.Reply.Error : x.Superseded ? "superseded" : "ok"), toPipeMs = x.Reached is long r ? Math.Round(Ms(x.Intended, r), 1) : (double?)null, newerArrivedMsBeforePipe = x.Reached != null && Staleness(x) > 0 ? Math.Round(Staleness(x), 1) : (double?)null }).ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { seconds, seed, stopMax, panicMax, obsolete = obsolete.Length, lastRight, commands = detail }, new JsonSerializerOptions { WriteIndented = true }));
            }
            result = pass ? 0 : 1;
        }
        catch (Exception ex) { log.WriteLine("PROBLEM: " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            try { capture?.Stop(); capture?.Dispose(); } catch (Exception) { }
            try
            {
                if (!await RealProbes.StoppedAndQuiet(inner)) log.WriteLine("CLEANUP PROBLEM: Soundpad did not stay stopped; the session was not ended.");
                else if (bridge != null) { await bridge.CleanupSession(); }
                bridge?.Dispose(); bridge = null;
                var after = await Settle(c => RealProbes.Same(c, before));
                bool same = RealProbes.Same(after, before);
                log.WriteLine($"final: {after.Entries.Length} sounds, {after.Categories.Length} categories (before: {before.Entries.Length}, {before.Categories.Length}); library identical to before: {same}");
                if (!same) { log.WriteLine("CLEANUP PROBLEM: check Soundpad for a \"Temp - Soundpad Web\" category"); if (result == 0) result = 1; }
            }
            catch (Exception ex) { log.WriteLine("CLEANUP PROBLEM: " + ex.Message); }
            bridge?.Dispose();
            await RealProbes.Delete(folder, log);
        }
        return result;
    }
}
