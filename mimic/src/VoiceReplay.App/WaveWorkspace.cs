using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VoiceReplay.Core;

namespace VoiceReplay.App;

public sealed class WaveWorkspace : UserControl, IDisposable
{
    private readonly Store store;
    private readonly ClipPipeline pipeline;
    private readonly CaptureService capture;
    private readonly Playback playback;
    private readonly Waveform wave = new() { Height = 220 };
    private readonly TextBlock mode = Ui.Text("LIVE BUFFER", 20), selection = Ui.Text(""), state = Ui.Text("Start monitoring to display incoming audio.", 12);
    private readonly TextBox start = new() { Width = 95 }, end = new() { Width = 95 }, title = new(), transcript = new() { Height = 64, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox loop = new() { Content = "Loop selection" };
    private readonly ComboBox view = new() { ItemsSource = new[] { 5, 10, 30 }, SelectedItem = 10, Width = 70 };
    private readonly StackPanel clipFields = new();
    private readonly ScrollViewer scroll;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stack<(float[] Samples, int Rate, double Offset)> undo = new();
    private readonly Button saveLive, trim, save, saveSpeech, undoButton, revertButton, reloadButton, zoomIn, zoomOut;
    private Clip? clip;
    private float[] samples = [];
    private int rate;
    private double offset, zoom = 1;
    private volatile bool live = true;
    private volatile bool displayActive = true;
    private bool disposed, dirty, loading, titleDirty, transcriptDirty;
    private int requestedSeconds = 10, requestedBins = 1200;
    private LiveFrame? latestFrame;
    private readonly CancellationTokenSource frameCancel = new();
    private readonly Task frameWorker;
    private long loadVersion;
    private LiveFrame? frame, dragFrame;
    private (Guid Id, long First, long Last)? liveSelection;
    private long playingFirst;
    private bool playingLive, testPlayback;
    private string shownPlaybackStatus = "Stopped";
    public event Action<string>? Message;
    public bool LiveMode => live;
    public Clip? CurrentClip => clip;
    public int FramesRendered
    {
        get; private set;
    }
    public double LastLiveFps
    {
        get; private set;
    }
    public void ResizeWave(double windowHeight)
    {
        wave.Height = Math.Clamp(windowHeight - 850 + 90, 90, 200);
        scroll.Height = wave.Height + 18;
        transcript.Height = windowHeight < 950 ? 36 : 64;
    }
    public Func<string> MonitorId { get; set; } = () => "";
    public Func<Clip?> SelectedClip { get; set; } = () => null;
    public void DisplayActive(bool active) => displayActive = active;
    public WaveWorkspace(Store store, ClipPipeline pipeline, CaptureService capture)
    {
        this.store = store;
        this.pipeline = pipeline;
        this.capture = capture;
        playback = new(m => store.Log("Preview: " + m));
        var compactButton = new Style(typeof(Button), (Style)FindResource(typeof(Button)));
        compactButton.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        compactButton.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 6, 3)));
        Resources[typeof(Button)] = compactButton;
        var compactText = new Style(typeof(TextBox), (Style)FindResource(typeof(TextBox)));
        compactText.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        compactText.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 4, 4)));
        Resources[typeof(TextBox)] = compactText;
        var panel = new StackPanel();
        Content = panel;
        zoomIn = Ui.Button("Zoom +", () => Zoom(2));
        zoomOut = Ui.Button("Zoom −", () => Zoom(.5));
        panel.Children.Add(Row(mode, Ui.Button("Live Buffer", ShowLive), Ui.AsyncButton("Clip Editor", () => ShowClip(clip ?? SelectedClip())), Ui.Text("Live view (s):"), view, zoomIn, zoomOut, Ui.Button("Select all", SelectAll)));
        scroll = new()
        {
            Content = wave,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 238
        };
        panel.Children.Add(scroll);
        scroll.SizeChanged += (_, _) => { wave.Width = Math.Max(1, scroll.ActualWidth - 20) * (live ? 1 : zoom); Volatile.Write(ref requestedBins, Math.Clamp((int)scroll.ActualWidth, 128, 2000)); };
        panel.Children.Add(Row(Ui.Text("Start (s)"), start, Ui.Text("End (s)"), end, Ui.Button("Apply points", ApplyPoints), selection));
        panel.Children.Add(Row(Ui.Button("Play All", () => Play(0, wave.Duration, false)), Ui.Button("Play Selection", () => Play(wave.Start, wave.End, loop.IsChecked == true)), Ui.Button("Play From Cursor", () => Play(wave.Cursor, wave.Duration, false)), Ui.Button("Stop", StopPlayback), loop));
        saveLive = Ui.AsyncButton("Save live selection", SaveLive);
        trim = Ui.Button("Trim to selection", Trim);
        save = Ui.AsyncButton("Save version", () => Save(false));
        saveSpeech = Ui.AsyncButton("Save + retranscribe", () => Save(true));
        undoButton = Ui.Button("Undo trim", Undo);
        revertButton = Ui.AsyncButton("Revert original", Revert);
        reloadButton = Ui.AsyncButton("Reload saved", () => ShowClip(clip, true));
        panel.Children.Add(Row(saveLive, trim, undoButton, revertButton, reloadButton, save, saveSpeech));
        var fields = new Grid();
        fields.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        fields.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(2, GridUnitType.Star)
        });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(Ui.Text("Title / generated filename", 12));
        left.Children.Add(title);
        fields.Children.Add(left);
        var right = new StackPanel();
        right.Children.Add(Ui.Text("Transcript", 12));
        right.Children.Add(transcript);
        Grid.SetColumn(right, 1);
        fields.Children.Add(right);
        clipFields.Children.Add(fields);
        panel.Children.Add(clipFields);
        panel.Children.Add(state);
        wave.SelectionBegan += () => dragFrame = frame;
        wave.SelectionChanged += () => { if (live && dragFrame is not null) { liveSelection = (dragFrame.BufferId, dragFrame.First + (long)Math.Round(wave.Start * dragFrame.Rate), dragFrame.First + (long)Math.Round(wave.End * dragFrame.Rate)); } UpdateSelection(); };
        wave.Seek += () => { if (playback.Playing && !testPlayback) try { double position = live && frame is not null ? (frame.First + wave.Cursor * frame.Rate - playingFirst) / frame.Rate : wave.Cursor; playback.Seek(position); } catch (Exception ex) { Error(ex); } };
        title.TextChanged += (_, _) => { if (!loading) { dirty = true; titleDirty = true; } };
        transcript.TextChanged += (_, _) => { if (!loading) { dirty = true; transcriptDirty = true; } };
        view.SelectionChanged += (_, _) => { Volatile.Write(ref requestedSeconds, (int)view.SelectedItem); frame = null; };
        timer.Tick += Tick;
        timer.Start();
        ShowLive();
        var frameToken = frameCancel.Token;
        frameWorker = Task.Run(() => BuildFrames(frameToken));
    }
    private static WrapPanel Row(params UIElement[] controls)
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls)
            panel.Children.Add(c);
        return panel;
    }
    private void Error(Exception e)
    {
        state.Text = e.Message;
        store.Log("Workspace", e);
        Message?.Invoke(e.Message);
    }
    private void UpdateMode()
    {
        mode.Text = live ? "LIVE BUFFER" : "CLIP EDITOR";
        clipFields.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        saveLive.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        trim.IsEnabled = !live;
        save.IsEnabled = !live && clip is not null;
        saveSpeech.IsEnabled = save.IsEnabled;
        view.IsEnabled = live;
        zoomIn.IsEnabled = !live;
        zoomOut.IsEnabled = !live;
        undoButton.IsEnabled = !live && undo.Count > 0;
        revertButton.IsEnabled = !live && clip is not null;
        reloadButton.IsEnabled = !live && clip is not null;
        wave.Width = Math.Max(1, scroll.ActualWidth - 20) * (live ? 1 : zoom);
    }
    public void ShowLive()
    {
        ++loadVersion;
        loading = false;
        StopPlayback();
        live = true;
        frame = null;
        int span = (int)view.SelectedItem;
        wave.Live(new(Guid.Empty, -span * 48000L, 0, 48000, span, new (float, float)[256]));
        wave.Start = 0;
        wave.End = span;
        wave.Cursor = span;
        UpdateMode();
        UpdateSelection();
        state.Text = "Drag to select historical audio. Capture keeps running while clips are edited.";
    }
    public async Task ShowClip(Clip? requested, bool reload = false)
    {
        if (requested is null)
        {
            state.Text = "Select a saved clip in the library.";
            return;
        }
        if (!reload && clip?.Id == requested.Id && samples.Length > 0)
        {
            ++loadVersion;
            loading = false;
            live = false;
            StopPlayback();
            wave.Load(samples, rate);
            UpdateMode();
            UpdateSelection();
            return;
        }
        if (!reload && dirty && clip is not null)
        {
            state.Text = "Save edits or use Reload saved before switching clips. The new recording is safely in the library.";
            return;
        }
        long version = ++loadVersion;
        loading = true;
        try
        {
            testPlayback = false;
            var data = await Task.Run(() => AudioFiles.Read(requested.AudioPath));
            if (disposed || version != loadVersion)
                return;
            StopPlayback();
            clip = requested;
            samples = data.Samples;
            rate = data.Rate;
            offset = requested.TrimStart;
            undo.Clear();
            title.Text = requested.Title;
            transcript.Text = requested.Transcript;
            dirty = false;
            titleDirty = false;
            transcriptDirty = false;
            live = false;
            wave.Load(samples, rate);
            UpdateMode();
            UpdateSelection();
            state.Text = $"{requested.Summary} · decoded {samples.Length} samples at {rate} Hz · peak {samples.Max(x => Math.Abs(x)):F4}";
            store.Log($"Opened clip {requested.Id} path={requested.AudioPath}; {rate}Hz; samples={samples.Length}; silent={AudioFiles.Silent(samples)}");
        }
        catch (Exception ex) { Error(ex); }
        finally { if (version == loadVersion) loading = false; }
    }
    private void Zoom(double factor)
    {
        if (live)
            return;
        zoom = Math.Clamp(zoom * factor, 1, 32);
        wave.Width = Math.Max(1, scroll.ActualWidth - 20) * zoom;
    }
    private void SelectAll()
    {
        wave.Start = 0;
        wave.End = wave.Duration;
        if (live && frame is not null)
            liveSelection = (frame.BufferId, Math.Max(capture.Buffer?.Oldest ?? 0, frame.First), frame.Last);
        UpdateSelection();
        wave.InvalidateVisual();
    }
    private void UpdateSelection()
    {
        start.Text = (wave.Start + wave.RulerOffset).ToString("F3", CultureInfo.InvariantCulture);
        end.Text = (wave.End + wave.RulerOffset).ToString("F3", CultureInfo.InvariantCulture);
        undoButton.IsEnabled = !live && undo.Count > 0;
        bool nonempty = liveSelection is { } chosen && chosen.Last > chosen.First;
        bool expired = nonempty && liveSelection is { } s && (capture.Buffer?.Id != s.Id || !capture.Buffer.Contains(s.First, s.Last));
        selection.Text = expired && live ? "Selection expired — select again" : live && !nonempty ? "Drag a non-empty live selection" : $"{(wave.End - wave.Start) * 1000:F1} ms · cursor {wave.Cursor:F3}s";
        saveLive.IsEnabled = live && nonempty && !expired;
    }
    private void ApplyPoints()
    {
        try
        {
            double a = double.Parse(start.Text, CultureInfo.InvariantCulture) - wave.RulerOffset, b = double.Parse(end.Text, CultureInfo.InvariantCulture) - wave.RulerOffset;
            if (!double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b > wave.Duration || b <= a)
                throw new ArgumentException("Choose a non-empty region inside the waveform.");
            wave.Start = a;
            wave.End = b;
            if (live && frame is not null)
                liveSelection = (frame.BufferId, frame.First + (long)Math.Round(a * frame.Rate), frame.First + (long)Math.Round(b * frame.Rate));
            UpdateSelection();
            wave.InvalidateVisual();
        }
        catch (Exception ex) { Error(ex); }
    }
    private (float[] Samples, int Rate, long First) ReadLive(double a, double b, bool selected)
    {
        var ring = capture.Buffer ?? throw new InvalidOperationException("Monitoring is paused. Start monitoring, then choose live audio.");
        var f = frame ?? throw new InvalidOperationException("No live waveform is available yet.");
        long first, last;
        if (selected && liveSelection is { } s)
        {
            if (s.Id != ring.Id)
                throw new InvalidOperationException("Selection expired after source restart.");
            first = s.First;
            last = s.Last;
        }
        else
        {
            first = f.First + (long)Math.Round(a * f.Rate);
            last = f.First + (long)Math.Round(b * f.Rate);
            first = Math.Max(first, ring.Oldest);
        }
        if (last <= first)
            throw new InvalidOperationException("Select a non-empty region containing buffered audio.");
        return (ring.ReadRange(first, last), ring.Rate, first);
    }
    private void Play(double a, double b, bool repeat)
    {
        try
        {
            testPlayback = false;
            if (live)
            {
                var audio = ReadLive(a, b, Math.Abs(a - wave.Start) < 1e-8 && Math.Abs(b - wave.End) < 1e-8 && liveSelection is not null);
                playingFirst = audio.First;
                playingLive = true;
                playback.Play(audio.Samples, audio.Rate, 0, audio.Samples.Length / (double)audio.Rate, MonitorId(), repeat);
            }
            else
            {
                playingLive = false;
                playback.Play(samples, rate, a, b, MonitorId(), repeat);
            }
            state.Text = playback.Status;
            shownPlaybackStatus = playback.Status;
        }
        catch (Exception ex) { Error(ex); }
    }
    public void TestPlayback()
    {
        try
        {
            playingLive = false;
            testPlayback = true;
            var tone = PreviewPlayer.TestTone();
            playback.Play(tone, 48000, 0, .4, MonitorId(), false);
            shownPlaybackStatus = playback.Status;
            state.Text = "400 ms low-level test tone. " + playback.Status + " — hearing it requires your listening confirmation.";
        }
        catch (Exception ex) { Error(ex); }
    }
    public void StopPlayback()
    {
        playback.Stop();
        shownPlaybackStatus = "Stopped";
        state.Text = "Stopped";
    }
    public void ClearClip(Guid id)
    {
        if (clip?.Id != id)
            return;
        StopPlayback();
        clip = null;
        samples = [];
        undo.Clear();
        dirty = false;
        ShowLive();
    }
    public void SyncMetadata()
    {
        if (clip is null || loading)
            return;
        loading = true;
        try
        {
            if (!titleDirty)
                title.Text = clip.Title;
            if (!transcriptDirty)
                transcript.Text = clip.Transcript;
        }
        finally { loading = false; }
    }
    private void Trim()
    {
        if (live)
        {
            state.Text = "Save the live selection first to edit it non-destructively.";
            return;
        }
        try
        {
            var data = AudioFiles.Trim(samples, rate, wave.Start, wave.End);
            PushUndo();
            offset += wave.Start;
            samples = data;
            StopPlayback();
            dirty = true;
            wave.Load(samples, rate);
            UpdateSelection();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void PushUndo()
    {
        if (undo.Count >= 24)
        {
            var keep = undo.Take(23).Reverse().ToArray();
            undo.Clear();
            foreach (var item in keep)
                undo.Push(item);
        }
        undo.Push((samples, rate, offset));
    }
    private void Undo()
    {
        if (live || undo.Count == 0)
            return;
        StopPlayback();
        (samples, rate, offset) = undo.Pop();
        dirty = true;
        wave.Load(samples, rate);
        UpdateSelection();
    }
    private async Task Revert()
    {
        if (clip is null || live)
            return;
        var audio = await Task.Run(() => AudioFiles.Read(clip.OriginalPath));
        PushUndo();
        samples = audio.Samples;
        rate = audio.Rate;
        offset = 0;
        dirty = false;
        StopPlayback();
        wave.Load(samples, rate);
        UpdateSelection();
    }
    private async Task SaveLive()
    {
        var data = ReadLive(wave.Start, wave.End, true);
        var saved = await pipeline.SaveCapture(data.Samples, data.Rate, capture.SourceName + " · selected buffer region");
        Message?.Invoke("Saved live selection: " + saved.Duration.ToString("F3") + "s");
        await ShowClip(saved);
    }
    private async Task Save(bool speech)
    {
        if (clip is null || live || samples.Length == 0)
            throw new InvalidOperationException("Open a saved clip first.");
        if (string.IsNullOrWhiteSpace(title.Text))
            throw new ArgumentException("Enter a title.");
        StopPlayback();
        var current = clip;
        var data = (float[])samples.Clone();
        int dataRate = rate;
        double trimOffset = offset;
        string newTitle = title.Text, newTranscript = transcript.Text;
        bool manualTitle = titleDirty, manualTranscript = transcriptDirty;
        await pipeline.Edit(current, () => { var next = new Clip { Id = current.Id, Title = manualTitle ? newTitle : current.Title, CapturedAt = current.CapturedAt, Version = current.Version + 1 }; string path = Path.Combine(Path.GetDirectoryName(current.AudioPath)!, Names.FileName(next)); AudioFiles.Write(path, data, dataRate); if (!current.RetainedPaths.Contains(current.AudioPath)) current.RetainedPaths.Add(current.AudioPath); current.Version = next.Version; current.Title = next.Title; if (manualTranscript) current.Transcript = newTranscript; current.AudioPath = path; current.Duration = data.Length / (double)dataRate; current.TrimStart = trimOffset; current.TrimEnd = trimOffset + current.Duration; current.TranscriptionState = "Edited — retry speech"; current.ImportState = "Pending"; current.Error = ""; });
        dirty = false;
        titleDirty = false;
        transcriptDirty = false;
        SyncMetadata();
        pipeline.Enqueue(current, speech);
        state.Text = "Saved version " + current.Version + ". Original and existing Soundpad backing files preserved.";
    }
    private async Task BuildFrames(CancellationToken token)
    {
        using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        try
        {
            while (await clock.WaitForNextTickAsync(token))
            {
                capture.UpdateClock();
                if (!live || !displayActive)
                    continue;
                var ring = capture.Buffer;
                if (ring is null)
                    continue;
                var next = ring.View(Volatile.Read(ref requestedSeconds), Volatile.Read(ref requestedBins));
                Volatile.Write(ref latestFrame, next);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { store.Log("Live waveform worker", ex); }
    }
    private void Tick(object? sender, EventArgs args)
    {
        if (disposed || !displayActive)
            return;
        try
        {
            if (shownPlaybackStatus != playback.Status)
            {
                shownPlaybackStatus = playback.Status;
                state.Text = playback.Status;
            }
            if (!testPlayback && !wave.IsMouseCaptured && (playback.Playing || playback.Status == "Finished"))
            {
                wave.Cursor = playingLive && frame is not null ? (playingFirst + playback.Position * frame.Rate - frame.First) / frame.Rate : playback.Position;
                wave.InvalidateVisual();
            }
            else if (playback.Status.StartsWith("Playback failed"))
                state.Text = playback.Status;
            if (!live || wave.IsMouseCaptured)
                return;
            var next = Volatile.Read(ref latestFrame);
            var ring = capture.Buffer;
            if (next is null || ring is null || next.BufferId != ring.Id || next.Seconds != (int)view.SelectedItem)
            {
                UpdateSelection();
                return;
            }
            if (ReferenceEquals(next, frame))
                return;
            frame = next;
            wave.Live(next);
            FramesRendered++;
            if (liveSelection is { } selected && selected.Id == ring.Id)
            {
                wave.Start = (selected.First - next.First) / (double)next.Rate;
                wave.End = (selected.Last - next.First) / (double)next.Rate;
            }
            else
            {
                wave.Start = 0;
                wave.End = wave.Duration;
            }
            wave.Cursor = playback.Playing && playingLive ? (playingFirst + playback.Position * next.Rate - next.First) / next.Rate : wave.Duration;
            UpdateSelection();
        }
        catch (Exception ex) { Error(ex); }
    }
    internal async Task SmokeEdit(Clip fixture)
    {
        await ShowClip(fixture);
        wave.Start = .2;
        wave.End = .7;
        Trim();
        if (samples.Length != rate / 2)
            throw new Exception("Inline trim mismatch");
        Undo();
        if (samples.Length != rate)
            throw new Exception("Inline undo mismatch");
        wave.Start = .2;
        wave.End = .7;
        Trim();
        await Save(false);
        var saved = AudioFiles.Read(fixture.AudioPath);
        if (saved.Samples.Length != saved.Rate / 2)
            throw new Exception("Inline save mismatch");
        await Revert();
        if (samples.Length != rate)
            throw new Exception("Inline original revert mismatch");
        dirty = false;
        ShowLive();
        await ShowClip(fixture);
    }
    internal async Task SmokeLive(Clip fixture)
    {
        ShowLive();
        var ring = capture.Buffer ?? throw new Exception("Live capture not running");
        await Task.Delay(200);
        int before = FramesRendered;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(1100);
        LastLiveFps = (FramesRendered - before) / watch.Elapsed.TotalSeconds;
        if (LastLiveFps < 18)
            throw new Exception($"Live waveform refresh below target: {LastLiveFps:F1} FPS");
        long samplesBefore = ring.Written;
        await ShowClip(fixture);
        await Task.Delay(200);
        if (!ReferenceEquals(capture.Buffer, ring) || ring.Written <= samplesBefore)
            throw new Exception("Editing interrupted live capture");
        ShowLive();
        await Task.Delay(100);
        if (!ReferenceEquals(capture.Buffer, ring))
            throw new Exception("Switching modes restarted capture");
    }
    internal async Task SmokePlayback()
    {
        TestPlayback();
        await Task.Delay(180);
        if (!playback.Playing || playback.Position <= 0)
            throw new Exception("WPF playback did not advance: " + playback.Status);
        await Task.Delay(400);
        var ring = capture.Buffer ?? throw new Exception("Preview stopped monitoring");
        if (ring.Snapshot(1).Max(x => Math.Abs(x)) < .005)
            throw new Exception("No actual preview signal in own-process loopback");
        if (playback.Playing || !state.Text.Contains("Finished"))
            throw new Exception("WPF playback completion status was not shown correctly");
        StopPlayback();
    }
    public void Dispose()
    {
        disposed = true;
        ++loadVersion;
        timer.Stop();
        frameCancel.Cancel();
        playback.Dispose();
    }
}
