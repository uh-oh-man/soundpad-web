using System.Diagnostics;
using System.Text.Json;
namespace SoundpadWeb.Testing;

/// <summary>
/// "--playback-bench" (2.35): two clients (the host and one guest) alternate plays, cue starts, regions and the
/// odd Stop as fast as people click, while queries, counter changes, library refreshes and an upload + reverse run
/// in the background. Everything goes through the public <see cref="Bridge"/> API against a
/// <see cref="MockSoundpad"/> with a throwaway data folder, so the same tool measures any companion version.
/// It never touches the real Soundpad, the companion's data or an audio device.
/// </summary>
public static class PlaybackBench
{
    /// <summary>Notes when the Bridge handed each play or stop to the pipe, before the pipe's own queueing.</summary>
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
    private sealed class Sent
    {
        public int Seq; public string Client = "", Op = "", Kind = ""; public string? SoundId; public int? Index; public bool Intent;
        public long Intended, Arrived, Acked; public Reply? Reply; public string? Failure; public Task? Task;
        public long? Reached; public long? NewerArrived;
        public bool Superseded => Reply is { Ok: true } && Flag("superseded") == true;
        public bool? Played => Reply is { Ok: true } ? Flag("played") : null;
        private bool? Flag(string name)
        {
            if (Reply?.Data == null) return null;
            using var data = JsonDocument.Parse(JsonSerializer.Serialize(Reply.Data, Json.Options));
            return data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        }
    }
    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
    private static object Distribution(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        double At(double p) => sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)], 1);
        return new { count = sorted.Length, p50 = At(0.5), p90 = At(0.9), p99 = At(0.99), max = sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 1) };
    }
    private static string Line(string label, object distribution)
    {
        using var d = JsonDocument.Parse(JsonSerializer.Serialize(distribution));
        var r = d.RootElement;
        return $"{label}: n={r.GetProperty("count")} p50={r.GetProperty("p50")} p90={r.GetProperty("p90")} p99={r.GetProperty("p99")} max={r.GetProperty("max")} ms";
    }

    public static async Task Run(string[] args)
    {
        string? Arg(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int latency = int.TryParse(Arg("--latency-ms"), out var l) && l is >= 0 and <= 1000 ? l : 5;
        int seconds = int.TryParse(Arg("--seconds"), out var s) && s is >= 1 and <= 600 ? s : 20;
        int seed = int.TryParse(Arg("--seed"), out var chosen) ? chosen : 1;
        // A newer intent that lands while a play is already on its way through the pipe cannot be honoured any more;
        // a play counts as obsolete only when the newer intent had arrived at least this long before it reached Soundpad.
        double grace = double.TryParse(Arg("--grace-ms"), System.Globalization.CultureInfo.InvariantCulture, out var g) && g >= 0 ? g : 3;
        var output = Arg("--out");
        var directory = Path.Combine(Path.GetTempPath(), "soundpad-web-playback-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var console = Console.Out;
        try
        {
            await using var mock = new MockSoundpad();
            // Eight library sounds with real files (waveform queries decode them), like the default harness mode.
            var all = System.Xml.Linq.XDocument.Parse(MockSoundpad.Sounds); var categories = System.Xml.Linq.XDocument.Parse(MockSoundpad.Categories);
            var template = all.Root!.Elements("Sound").First();
            for (int i = 5; i <= 8; i++) { var item = new System.Xml.Linq.XElement(template); item.SetAttributeValue("index", i); item.SetAttributeValue("title", $"Bench sound {i}"); item.SetAttributeValue("url", $@"C:\Private\bench-{i}.wav"); all.Root.Add(item); }
            foreach (var element in all.Descendants("Sound").Concat(categories.Descendants("Sound"))) { var original = (string?)element.Attribute("url"); if (original == null) continue; var path = Path.Combine(directory, Path.GetFileName(original)); if (!File.Exists(path)) AudioFixtures.Wave(path); element.SetAttributeValue("url", path); element.SetAttributeValue("duration", "00:02.500"); }
            mock.AllXml = all.ToString(); mock.CategoryXml = categories.ToString();
            using var inner = new SoundpadPipe(mock.Name, true);
            var pipe = new TimedPipe(inner);
            using var bridge = new Bridge(pipe, new StateStore(directory));
            bridge.UseMimicCapture(new MimicFixture()); bridge.UseTranscriptEngine(new TranscriptFixture());
            bridge.UseHotkeys(key => new HotkeyFixture(key)); bridge.UseVoiceReplayLibrary(VoiceReplayFixture.Create(Path.Combine(directory, "voice-replay")));
            Console.SetOut(TextWriter.Null); // the mock prints a TEST_PLAY line for every play
            await bridge.Poll(true);

            const string audience = "bench-audience", guestPeer = "bench-guest-peer";
            var hostPerson = new Participant(Guid.NewGuid().ToString(), "Bench Host"); var guestPerson = new Participant(Guid.NewGuid().ToString(), "Bench Guest");
            Command Cmd(string op) => new(2, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), op);
            var begin = await bridge.Execute(Cmd("beginSession") with { AudienceId = audience }, "host");
            if (!begin.Ok) throw new InvalidOperationException("beginSession: " + begin.Error);
            static string? AssetId(Reply reply) { if (reply.Data == null) return null; using var data = JsonDocument.Parse(JsonSerializer.Serialize(reply.Data, Json.Options)); return data.RootElement.TryGetProperty("assetId", out var id) ? id.GetString() : null; }
            // One Temp sound that is already prepared, as a clip made earlier in the session would be.
            var tempWave = Path.Combine(directory, "bench-temp-source.wav"); AudioFixtures.Wave(tempWave, 1800);
            var uploaded = await bridge.Upload(File.ReadAllBytes(tempWave), "Bench temp.wav", "Bench Host", true, "host", audience);
            if (!uploaded.Ok) throw new InvalidOperationException("upload: " + uploaded.Error);
            var tempAsset = AssetId(uploaded);
            await bridge.Poll(true);
            var tempSound = bridge.Snapshot.Library.Sounds.First(x => x.AssetId == tempAsset && !x.InternalVariant);
            var natives = bridge.Snapshot.Library.Sounds.Where(x => x.AssetId == null).ToArray();
            // Snapshot sounds are in Soundpad's list order, so position gives each sound's play index.
            int IndexOf(string soundId)
            {
                var sounds = bridge.Snapshot.Library.Sounds; int at = Array.FindIndex(sounds, x => x.Id == soundId);
                var entries = System.Xml.Linq.XDocument.Parse(mock.AllXml).Root!.Elements("Sound").ToArray();
                return at >= 0 && at < entries.Length ? (int)entries[at].Attribute("index")! : -1;
            }
            var indexes = natives.Append(tempSound).ToDictionary(x => x.Id, x => IndexOf(x.Id));

            // The cost of one request with no simulated latency, for reading the numbers below.
            var probe = Stopwatch.StartNew(); for (int i = 0; i < 20; i++) await inner.Request("IsAlive()"); double bareRoundTrip = probe.Elapsed.TotalMilliseconds / 20;
            mock.LatencyMs = latency;
            probe.Restart(); for (int i = 0; i < 20; i++) await inner.Request("IsAlive()"); double roundTrip = probe.Elapsed.TotalMilliseconds / 20;
            lock (mock.Timeline) mock.Timeline.Clear(); lock (pipe.Calls) pipe.Calls.Clear();

            var sent = new List<Sent>(); var order = new object(); int sequence = 0;
            // One caller at a time, like the single loopback socket: commands reach Execute in a definite order.
            Sent Send(string client, Command command, string kind, bool intent, string actor, Participant? person, string? requester)
            {
                var item = new Sent { Client = client, Op = command.Op, Kind = kind, SoundId = command.SoundId, Intent = intent, Intended = Stopwatch.GetTimestamp() };
                if (command.SoundId != null && indexes.TryGetValue(command.SoundId, out int index)) item.Index = index;
                lock (order)
                {
                    item.Seq = ++sequence; sent.Add(item);
                    Task<Reply> running;
                    try { running = bridge.Execute(command with { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, actor, person, requester, actor == "host" ? null : audience); }
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
                    await Task.Delay(random.Next(20, 151));
                    int roll = random.Next(100);
                    // Mostly library sounds; one in six targets the prepared Temp sound.
                    var target = random.Next(6) == 0 ? tempSound : natives[random.Next(natives.Length)];
                    string kind = target.Id == tempSound.Id ? "temp" : "native";
                    var command = Cmd(roll < 55 ? "play" : roll < 75 ? "startAt" : roll < 92 ? "region" : "stop") with { SoundId = target.Id, LibraryVersion = bridge.Snapshot.Library.Version };
                    if (command.Op == "stop") { command = command with { SoundId = null, LibraryVersion = null }; kind = "stop"; }
                    else if (command.Op == "startAt") command = command with { PositionMs = random.Next(0, 1200) };
                    else if (command.Op == "region") { long from = random.Next(0, 1000); command = command with { PositionMs = from, EndMs = from + random.Next(200, 700) }; }
                    Send(name, command, kind, true, actor, person, requester);
                }
            }
            int polls = 0, pollFailures = 0; string? pollFailure = null;
            async Task PollLoop()
            {
                // The companion's own main loop: playback sample every 250 ms, library refresh every 2 s here.
                long next = 0;
                while (!stop.IsCancellationRequested)
                {
                    bool refresh = clock.ElapsedMilliseconds >= next; if (refresh) next = clock.ElapsedMilliseconds + 2000;
                    try { await bridge.Poll(refresh); polls++; }
                    catch (Exception ex) { pollFailures++; pollFailure = ex.GetType().Name + ": " + ex.Message; } // the real companion would have ended here
                    try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { }
                }
            }
            async Task Queries()
            {
                var random = new Random(seed + 100);
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    await Task.Delay(random.Next(60, 140));
                    int pick = random.Next(10); var sound = natives[random.Next(natives.Length)];
                    if (pick < 5) Send("load", Cmd("waveform") with { SoundId = sound.Id }, "waveform", false, random.Next(2) == 0 ? "host" : "guest", null, guestPeer);
                    else if (pick < 8) Send("load", Cmd("statistics") with { SoundId = pick == 7 ? sound.Id : null }, "statistics", false, "host", hostPerson, null);
                    else Send("load", Cmd("counter") with { Delta = random.Next(2) == 0 ? 1 : -1 }, "counter", false, "host", hostPerson, null);
                }
            }
            int uploads = 0, uploadFailures = 0;
            async Task ManagedWork()
            {
                // A new clip and its first reverse every 3 s: decoding, hashing, importing and an autoplay that
                // finishes long after it was asked for. The last one starts well before the end of the run.
                for (int round = 0; clock.Elapsed.TotalSeconds < seconds - 2.5; round++)
                {
                    var wave = Path.Combine(directory, $"bench-upload-{round}.wav"); AudioFixtures.Wave(wave, 1500 + round * 41);
                    var reply = await bridge.Upload(File.ReadAllBytes(wave), $"Bench clip {round}.wav", "Bench Guest", false, guestPeer, audience);
                    uploads++; if (!reply.Ok) uploadFailures++;
                    var clip = reply.Ok ? bridge.Snapshot.Library.Sounds.FirstOrDefault(x => x.AssetId == AssetId(reply)) : null;
                    if (clip != null) Send("load", Cmd("reverse") with { SoundId = clip.Id, LibraryVersion = bridge.Snapshot.Library.Version }, "reverse", true, "guest", guestPerson, guestPeer);
                    try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { }
                }
            }
            var poller = PollLoop();
            await Task.WhenAll(Client("host", "host", hostPerson, null, seed), Client("guest", "guest", guestPerson, guestPeer, seed + 1), Queries(), ManagedWork());
            double ranFor = clock.Elapsed.TotalSeconds;
            // Let every command answer (a backlog on older code can take a while), then stop polling.
            Task[] pending; lock (order) pending = sent.Select(x => x.Task!).ToArray();
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30)); } catch (TimeoutException) { }
            double drainedAt = clock.Elapsed.TotalSeconds;
            stop.Cancel(); await poller;
            Console.SetOut(console);

            (long At, string Command)[] timeline; lock (mock.Timeline) timeline = mock.Timeline.ToArray();
            (long Issued, long Returned, string Command)[] calls; lock (pipe.Calls) calls = pipe.Calls.ToArray();
            Sent[] commands; lock (order) commands = sent.ToArray();
            var intents = commands.Where(x => x.Intent).OrderBy(x => x.Seq).ToArray();
            for (int i = 0; i + 1 < intents.Length; i++) intents[i].NewerArrived = intents[i + 1].Arrived;

            // Match each DoPlaySound Soundpad received to the command that caused it: the pipe call that was open
            // at that moment, then the command that was open around that call and answers first.
            int unattributed = 0; var claimed = new HashSet<Sent>();
            foreach (var arrival in timeline.Where(e => e.Command.StartsWith("DoPlaySound(")))
            {
                int index = int.Parse(arrival.Command[12..^1]);
                var call = calls.Where(c => c.Command == arrival.Command && c.Issued <= arrival.At && c.Returned >= arrival.At).OrderByDescending(c => c.Issued).FirstOrDefault();
                if (call.Command == null) { unattributed++; continue; }
                var owner = intents.Where(x => !claimed.Contains(x) && x.Op != "stop" && x.Intended <= call.Issued && (x.Acked == 0 || x.Acked >= call.Returned) && x.Index == index).OrderBy(x => x.Acked == 0 ? long.MaxValue : x.Acked).FirstOrDefault()
                    ?? intents.Where(x => !claimed.Contains(x) && x.Op == "reverse" && x.Intended <= call.Issued && (x.Acked == 0 || x.Acked >= call.Returned)).OrderBy(x => x.Acked == 0 ? long.MaxValue : x.Acked).FirstOrDefault();
                if (owner == null) { unattributed++; continue; }
                claimed.Add(owner); owner.Reached = arrival.At;
            }
            var started = intents.Where(x => x.Reached != null).ToArray();
            double Staleness(Sent x) => x.NewerArrived is long newer ? Ms(newer, x.Reached!.Value) : double.NegativeInfinity;
            var obsolete = started.Where(x => Staleness(x) >= grace).ToArray();
            var overtaken = started.Where(x => Staleness(x) > 0 && Staleness(x) < grace).ToArray();
            var playback = intents.Where(x => x.Kind != "reverse").ToArray();
            var acked = intents.Where(x => x.Reply != null).ToArray();
            var errors = intents.Where(x => x.Reply is { Ok: false } || x.Failure != null).GroupBy(x => x.Failure ?? x.Reply!.Error ?? "(no message)").ToDictionary(group => group.Key, group => group.Count());
            var stops = intents.Where(x => x.Op == "stop" && x.Acked != 0).ToArray();
            var load = commands.Where(x => !x.Intent).ToArray();
            var loadErrors = load.Where(x => x.Reply is { Ok: false } || x.Failure != null).GroupBy(x => x.Kind + ": " + (x.Failure ?? x.Reply!.Error ?? "(no message)")).ToDictionary(group => group.Key, group => group.Count());

            // Is what Soundpad ended up doing what the last intent asked for?
            var last = intents.LastOrDefault();
            var lastPlay = timeline.LastOrDefault(e => e.Command.StartsWith("DoPlaySound("));
            string expected = last == null ? "nothing" : last.Op == "stop" ? "stopped" : last.Index is int wanted ? $"DoPlaySound({wanted})" : "the reversed clip";
            string actual; bool matches;
            if (last == null) { actual = "nothing"; matches = true; }
            else if (last.Op == "stop")
            {
                // Nothing may start after the final Stop arrived (a play already on the wire within the grace aside).
                bool later = lastPlay.Command != null && Ms(last.Arrived, lastPlay.At) >= grace;
                actual = later ? $"{lastPlay.Command} started {Ms(last.Arrived, lastPlay.At):0} ms after the final Stop arrived" : "stopped"; matches = !later;
            }
            else
            {
                actual = lastPlay.Command ?? "nothing played"; matches = last.Reached != null && last.Reached == lastPlay.At;
            }

            var result = new
            {
                tool = "playback-bench",
                companionVersion = bridge.Snapshot.CompanionVersion,
                settings = new { latencyMs = latency, seconds, seed, graceMs = grace },
                ranForSeconds = Math.Round(ranFor, 1),
                allAnsweredAfterSeconds = Math.Round(drainedAt, 1),
                pipeRoundTripMs = new { withoutLatency = Math.Round(bareRoundTrip, 2), withLatency = Math.Round(roundTrip, 2) },
                intents = new
                {
                    total = intents.Length,
                    byOp = intents.GroupBy(x => x.Op).ToDictionary(group => group.Key, group => group.Count()),
                    byTarget = intents.GroupBy(x => x.Kind).ToDictionary(group => group.Key, group => group.Count())
                },
                acks = new
                {
                    ok = acked.Count(x => x.Reply!.Ok && !x.Superseded && x.Played != false),
                    superseded = acked.Count(x => x.Superseded || x.Played == false),
                    errors = errors.Values.Sum(),
                    unanswered = intents.Count(x => x.Acked == 0),
                    errorMessages = errors
                },
                latencyMs = new
                {
                    sendToSoundpadForPlaysThatStarted = Distribution(started.Select(x => Ms(x.Intended, x.Reached!.Value))),
                    sendToAckForAllPlaybackCommands = Distribution(intents.Where(x => x.Acked != 0).Select(x => Ms(x.Intended, x.Acked))),
                    sendToAckForStop = Distribution(stops.Select(x => Ms(x.Intended, x.Acked)))
                },
                plays = new
                {
                    reachedSoundpad = timeline.Count(e => e.Command.StartsWith("DoPlaySound(")),
                    obsolete = obsolete.Length,
                    obsoleteWorstMsAfterNewerIntent = obsolete.Length == 0 ? 0 : Math.Round(obsolete.Max(Staleness), 1),
                    overtakenWhileOnTheWire = overtaken.Length,
                    notMatchedToACommand = unattributed,
                    answeredOkButSupersededAndStillPlayed = started.Count(x => x.Superseded)
                },
                longestStopWaitMs = stops.Length == 0 ? 0 : Math.Round(stops.Max(x => Ms(x.Intended, x.Acked)), 1),
                final = new { lastIntent = last == null ? null : last.Op + (last.Kind is "native" or "temp" ? " (" + last.Kind + ")" : ""), lastIntentClient = last?.Client, expected, actual, matches, mockStatus = mock.Status, mockPlayingIndex = mock.PlayingIndex },
                background = new
                {
                    queries = load.GroupBy(x => x.Kind).ToDictionary(group => group.Key, group => group.Count()),
                    errors = loadErrors.Values.Sum(),
                    errorMessages = loadErrors,
                    sendToAckMs = Distribution(load.Where(x => x.Acked != 0).Select(x => Ms(x.Intended, x.Acked))),
                    uploads, uploadFailures, polls, pollFailures, pollFailure
                },
                commands = commands.Select(x => new
                {
                    seq = x.Seq, client = x.Client, op = x.Op, target = x.Kind, index = x.Index,
                    sentMs = Math.Round(Ms(commands[0].Intended, x.Intended), 1),
                    ackMs = x.Acked == 0 ? (double?)null : Math.Round(Ms(x.Intended, x.Acked), 1),
                    ack = x.Failure ?? (x.Reply == null ? "unanswered" : !x.Reply.Ok ? "error" : x.Superseded || x.Played == false ? "superseded" : "ok"),
                    error = x.Reply is { Ok: false } ? x.Reply.Error : null,
                    reachedSoundpadMs = x.Reached is long reached ? Math.Round(Ms(x.Intended, reached), 1) : (double?)null,
                    newerIntentArrivedMsBeforePlay = x.Reached != null && x.NewerArrived != null && Staleness(x) > 0 ? Math.Round(Staleness(x), 1) : (double?)null
                }).ToArray()
            };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            if (output != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, json); }

            Console.WriteLine($"Playback bench: companion {result.companionVersion}, mock Soundpad with {latency} ms per request, {seconds} s, seed {seed}");
            Console.WriteLine($"One pipe round trip: {bareRoundTrip:0.0} ms bare, {roundTrip:0.0} ms with the simulated latency");
            Console.WriteLine($"Playback commands: {intents.Length} ({string.Join(", ", result.intents.byOp.Select(p => p.Value + " " + p.Key))}); to the prepared Temp sound: {intents.Count(x => x.Kind == "temp")}");
            Console.WriteLine($"Answers: {result.acks.ok} ok, {result.acks.superseded} superseded, {result.acks.errors} errors, {result.acks.unanswered} unanswered");
            foreach (var (message, count) in errors.OrderByDescending(p => p.Value)) Console.WriteLine($"  error x{count}: {message}");
            Console.WriteLine(Line("Send -> reached Soundpad (plays that started)", result.latencyMs.sendToSoundpadForPlaysThatStarted));
            Console.WriteLine(Line("Send -> answer (all playback commands)", result.latencyMs.sendToAckForAllPlaybackCommands));
            Console.WriteLine(Line("Send -> answer (Stop)", result.latencyMs.sendToAckForStop));
            Console.WriteLine($"Plays that reached Soundpad: {result.plays.reachedSoundpad}; obsolete (a newer intent had arrived {grace:0.#}+ ms earlier): {obsolete.Length}{(obsolete.Length > 0 ? $", worst {result.plays.obsoleteWorstMsAfterNewerIntent} ms late" : "")}; overtaken while on the wire (< {grace:0.#} ms): {overtaken.Length}; unmatched: {unattributed}");
            Console.WriteLine($"Longest Stop wait: {result.longestStopWaitMs} ms");
            Console.WriteLine($"Final state: last intent {result.final.lastIntent} from the {result.final.lastIntentClient}; expected {expected}; got {actual}; {(matches ? "MATCHES" : "DOES NOT MATCH")}");
            Console.WriteLine($"Background: {string.Join(", ", result.background.queries.Select(p => p.Value + " " + p.Key))}; {uploads} uploads ({uploadFailures} failed); {polls} polls ({pollFailures} failed{(pollFailure != null ? ": " + pollFailure : "")}); {result.background.errors} error answers");
            foreach (var (message, count) in loadErrors.OrderByDescending(p => p.Value)) Console.WriteLine($"  background error x{count}: {message}");
            Console.WriteLine(Line("Background send -> answer", result.background.sendToAckMs));
            if (output != null) Console.WriteLine("Details: " + Path.GetFullPath(output));
        }
        finally
        {
            Console.SetOut(console);
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
