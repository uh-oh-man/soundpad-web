using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Windows.Media;
using VoiceReplay.Core;
using Forms = System.Windows.Forms;

namespace VoiceReplay.App;

public sealed class MainWindow : Window
{
    private readonly Store store;
    private readonly CaptureService capture = new();
    private readonly SoundpadService soundpad = new(new PipeTransport());
    private readonly LocalTranscriber speech;
    private readonly ClipPipeline pipeline;
    private readonly WaveWorkspace workspace;
    private readonly ComboBox monitors = new() { DisplayMemberPath = "Label", MinWidth = 300, MaxWidth = 520 };
    private bool refreshingLibrary;
    private readonly ComboBox sources = new() { MinWidth = 350, MaxWidth = 650, DisplayMemberPath = "Name" };
    private readonly ComboBox duration = new() { ItemsSource = new[] { 3, 5, 10, 15, 30 } };
    private readonly TextBlock status = Ui.Text("Paused"), buffer = Ui.Text("0 seconds buffered"), sp = Ui.Text("Soundpad: checking…"), notice = Ui.Text("Choose a source, run diagnostics, then start monitoring. Preview requires a physical output in Settings.");
    private readonly ProgressBar meter = new() { Minimum = 0, Maximum = 1, Height = 3, Foreground = new SolidColorBrush(Color.FromRgb(79, 213, 190)), Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBox search = new() { ToolTip = "Search transcript, title, or filename" };
    private readonly ListBox library = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Forms.NotifyIcon tray;
    private Hotkeys? hotkeys;
    private bool exiting, checking, wantMonitoring;
    private DateTime nextCheck;
    private readonly bool smoke;
    private readonly string? testOutput;

    public MainWindow(Store store, bool smoke, string? testOutput = null)
    {
        Ui.Window(this);
        this.store = store;
        this.smoke = smoke;
        this.testOutput = testOutput;
        speech = new(store);
        pipeline = new(store, speech, soundpad);
        workspace = new(store, pipeline, capture)
        {
            MonitorId = () => store.Settings.MonitorDeviceId,
            SelectedClip = () => library.SelectedItem as Clip
        };
        workspace.Message += message => notice.Text = message;
        Title = "Voice Replay Studio V1.1";
        Width = 1280;
        Height = 1060;
        MinWidth = 1000;
        MinHeight = 850;
        if (smoke)
        {
            ShowActivated = false;
            Left = -20000;
            Top = -20000;
            ShowInTaskbar = false;
        }
        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(workspace);
        top.Children.Add(Ui.Row(Ui.AsyncButton("Start monitoring", Start), Ui.Button("Pause monitoring", Pause), duration, Ui.AsyncButton("Capture now", Capture), Ui.AsyncButton("Source diagnostic", Diagnose)));
        top.Children.Add(Ui.Row(Ui.Text("Record from:"), sources, Ui.Button("Refresh devices", () => { RefreshSources(); RefreshMonitors(); })));

        top.Children.Add(Ui.Row(Ui.Text("Monitor out:"), monitors, Ui.Button("Test Playback", workspace.TestPlayback), Ui.AsyncButton("Output diagnostic", () => { notice.Text = MonitorOutputs.SessionDiagnostic(store.Settings.MonitorDeviceId); store.Log(notice.Text); return Task.CompletedTask; })));
        top.Children.Add(meter);
        var readouts = new Grid();
        readouts.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(2, GridUnitType.Star)
        });
        readouts.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(2, GridUnitType.Star)
        });
        readouts.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        status.FontSize = 12;
        buffer.FontSize = 12;
        sp.FontSize = 12;
        readouts.Children.Add(status);
        Grid.SetColumn(buffer, 1);
        readouts.Children.Add(buffer);
        Grid.SetColumn(sp, 2);
        readouts.Children.Add(sp);
        top.Children.Add(readouts);
        notice.Foreground = new SolidColorBrush(Color.FromRgb(245, 201, 122));
        top.Children.Add(notice);
        search.Width = 500;
        top.Children.Add(Ui.Row(Ui.Text("CLIP LIBRARY · search", 16), search));
        duration.SelectedItem = store.Settings.CaptureSeconds;
        duration.SelectionChanged += (_, _) => { if (duration.SelectedItem is int n) { store.Settings.CaptureSeconds = n; store.Save(); } };
        monitors.SelectionChanged += (_, _) => { if (monitors.SelectedItem is MonitorOutput d) { workspace.StopPlayback(); store.Settings.MonitorDeviceId = d.Id; store.Save(); notice.Text = d.Allowed ? "Preview output selected: " + d.Name : "This endpoint is blocked: " + d.Reason; } };
        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        bottom.Children.Add(Ui.Row(Ui.AsyncButton("Retry speech", () => Retry(true)), Ui.AsyncButton("Import / retry", () => Retry(false)), Ui.Button("Open location", OpenLocation), Ui.AsyncButton("Delete local", Delete), Ui.Button("Settings", OpenSettings), Ui.Button("Hide to tray", Hide)));
        bottom.Children.Add(Ui.Text("Monitoring continues during editing. Close/minimize → tray. Tray → Exit stops the application.", 12));
        library.ItemTemplate = (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel><TextBlock Text="{Binding Title}" FontSize="17" FontWeight="SemiBold" TextWrapping="Wrap"/><TextBlock Text="{Binding Transcript}" TextWrapping="Wrap" Opacity="0.8" Margin="0,4,0,0"/><TextBlock Text="{Binding Summary}" FontSize="12" Opacity="0.65" TextWrapping="Wrap" Margin="0,6,0,0"/><TextBlock Text="{Binding Error}" Foreground="#F5C97A" FontSize="12" TextWrapping="Wrap"/></StackPanel></DataTemplate>
        """);
        root.Children.Add(library);
        search.TextChanged += (_, _) => RefreshLibrary();
        library.SelectionChanged += (_, _) => { if (!refreshingLibrary && library.SelectedItem is Clip c) _ = workspace.ShowClip(c); };
        pipeline.Changed += () => Dispatcher.BeginInvoke(() => { RefreshLibrary(); workspace.SyncMetadata(); });
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Voice Replay Studio — paused", Visible = !smoke };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMain());
        menu.Items.Add("Capture", null, async (_, _) => await Safe(Capture));
        menu.Items.Add("Pause monitoring", null, (_, _) => Pause());
        menu.Items.Add("Resume monitoring", null, async (_, _) => await Safe(Start));
        menu.Items.Add("Exit", null, async (_, _) => await Exit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowMain();
        SourceInitialized += (_, _) => { if (!smoke) { hotkeys = new(this, id => { if (id == 1) _ = Safe(Capture); else if (id == 2) { workspace.StopPlayback(); _ = Safe(() => soundpad.Stop()); } else OpenEditor(); }); try { hotkeys.Set(store.Settings); } catch (Exception ex) { notice.Text = ex.Message; } } };
        Loaded += async (_, _) =>
        {
            RefreshSources();
            RefreshMonitors();
            RefreshLibrary();
            if (smoke)
            {
                await Smoke();
                return;
            }
            timer.Tick += Tick;
            timer.Start();
            if (store.Settings.StartMinimized)
                Hide();
            foreach (var c in store.Snapshot().Where(x => x.TranscriptionState == "Pending"))
                pipeline.Enqueue(c, true);
        };
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        SizeChanged += (_, _) => workspace.ResizeWave(ActualHeight);
        IsVisibleChanged += (_, _) => workspace.DisplayActive(IsVisible);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
    }
    private async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            store.Log("Operation failed", ex);
            notice.Text = ex.Message;
            if (!IsVisible && !smoke)
                tray.ShowBalloonTip(3000, "Voice Replay Studio", ex.Message, Forms.ToolTipIcon.Warning);
        }
    }
    private void ShowMain()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    private void RefreshSources()
    {
        var current = sources.SelectedItem as AudioSource;
        var items = CaptureService.Sources();
        sources.ItemsSource = items;
        string desiredKind = current?.Kind ?? store.Settings.SourceKind;
        sources.SelectedItem = items.FirstOrDefault(x => x.Id == (current?.Id ?? store.Settings.SourceId) && x.Kind == desiredKind)
            ?? (desiredKind == "Process" ? items.FirstOrDefault(x => x.Kind == "Process" && x.ProcessName == (current?.ProcessName ?? store.Settings.ProcessName)) : null);
        if (sources.SelectedItem is null && string.IsNullOrEmpty(store.Settings.SourceId))
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var d = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                sources.SelectedItem = items.FirstOrDefault(x => x.Id == d.ID);
            }
            catch { }
        }
    }
    private void RefreshLibrary()
    {
        var selected = (library.SelectedItem as Clip)?.Id;
        string q = search.Text;
        refreshingLibrary = true;
        library.ItemsSource = store.Snapshot().Where(x => (x.Title + " " + x.Transcript + " " + Path.GetFileName(x.AudioPath)).Contains(q, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.CapturedAt).ToArray();
        library.SelectedItem = library.Items.Cast<Clip>().FirstOrDefault(x => x.Id == selected) ?? library.Items.Cast<Clip>().FirstOrDefault();
        refreshingLibrary = false;
    }
    private async Task Start()
    {
        if (sources.SelectedItem is not AudioSource source)
            throw new InvalidOperationException("Choose an audio source.");
        store.Settings.SourceId = source.Id;
        store.Settings.SourceKind = source.Kind;
        store.Settings.ProcessName = source.ProcessName;
        store.Save();
        wantMonitoring = true;
        await capture.Start(source, store.Settings.BufferSeconds);
        notice.Text = capture.Running ? "Monitoring stays active in the tray. Capture hotkey: " + store.Settings.CaptureHotkey : "Monitoring start was canceled.";
    }
    private void Pause()
    {
        wantMonitoring = false;
        capture.Stop();
        notice.Text = "Monitoring paused. No audio is being captured.";
    }
    private async Task Capture()
    {
        // Snapshot synchronously at the key event; all disk work and inference happen elsewhere.
        var samples = capture.Snapshot(store.Settings.CaptureSeconds);
        int rate = capture.Buffer!.Rate;
        string source = capture.SourceName;
        int targetRate = store.Settings.SampleRate;
        if (rate != targetRate)
            samples = await Task.Run(() => AudioFiles.Resample(samples, rate, targetRate));
        var clip = await pipeline.SaveCapture(samples, targetRate, source);
        notice.Text = $"Saved {clip.Duration:F2}s — speech and Soundpad processing queued.";
        await workspace.ShowClip(clip);
    }
    private Clip Selected() => library.SelectedItem as Clip ?? throw new InvalidOperationException("Select a clip first.");
    private void OpenEditor()
    {
        if (!IsVisible)
            ShowMain();
        if (library.SelectedItem is Clip c)
            _ = workspace.ShowClip(c);
    }
    private void RefreshMonitors()
    {
        var chosen = store.Settings.MonitorDeviceId;
        monitors.ItemsSource = MonitorOutputs.List();
        monitors.SelectedItem = monitors.Items.Cast<MonitorOutput>().FirstOrDefault(x => x.Id == chosen);
        if (monitors.SelectedItem is null)
            notice.Text = "Choose your Monitor output, then press Test Playback. Recording source is independent.";
    }
    private Task Retry(bool speechRetry)
    {
        var c = Selected();
        pipeline.Enqueue(c, speechRetry, !speechRetry);
        return Task.CompletedTask;
    }
    private void OpenLocation()
    {
        if (library.SelectedItem is Clip c)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{c.AudioPath}\"") { UseShellExecute = true });
    }
    private async Task Delete()
    {
        var c = Selected();
        if (MessageBox.Show("Remove this clip from the local library? Unimported local audio will be deleted. Files backing Soundpad entries are retained. Soundpad entries will not be removed.", "Delete local clip", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        workspace.ClearClip(c.Id);
        await pipeline.Edit(c, () =>
        {
            // Never remove a directory recursively: imported versions may still be referenced externally.
            foreach (var path in new[] { c.OriginalPath, c.AudioPath }.Distinct())
                if (path != c.ImportedPath && !c.RetainedPaths.Contains(path) && File.Exists(path))
                    File.Delete(path);
            store.Remove(c);
        });
        RefreshLibrary();
    }
    private async Task Diagnose()
    {
        bool wasRunning = capture.Running;
        if (!wasRunning)
            await Start();
        notice.Text = "Diagnostic: listening for 3 seconds without playing or rerouting audio…";
        await Task.Delay(3000);
        int n = capture.Buffer?.Count ?? 0;
        bool signal = false;
        if (n > 0)
            signal = !AudioFiles.Silent(capture.Buffer!.Snapshot(3));
        var result = $"Source: {capture.SourceName}\nStatus: {capture.Status}\nBuffered frames: {n}\nSignal detected: {signal}\nNo routing or default devices were changed.\nA silent application must produce sound to complete the signal check.";
        store.Log("Source diagnostic: " + result);
        if (!wasRunning)
            Pause();
        MessageBox.Show(result, "Source diagnostic");
    }
    private async void Tick(object? sender, EventArgs e)
    {
        capture.UpdateClock();
        meter.Value = capture.Peak;
        status.Text = capture.Status + (capture.Running ? " · " + capture.SourceName : "") + (capture.Running && DateTime.Now - capture.LastSignal > TimeSpan.FromSeconds(5) ? " · silent for 5+ seconds" : "");
        buffer.Text = $"{(capture.Buffer is null ? 0 : capture.Buffer.Count / (double)capture.Buffer.Rate):F1}s / {store.Settings.BufferSeconds}s buffered  ·  save last {store.Settings.CaptureSeconds}s  ·  {store.Settings.CaptureHotkey}";
        tray.Text = capture.Running ? "Voice Replay Studio — monitoring" : "Voice Replay Studio — paused / disconnected";
        if (checking || DateTime.Now < nextCheck)
            return;
        checking = true;
        nextCheck = DateTime.Now.AddSeconds(10);
        try
        {
            await soundpad.Probe();
            sp.Text = "Soundpad: " + soundpad.Status;
            if (wantMonitoring && capture.Running && store.Settings.SourceKind == "Process" && !System.Diagnostics.Process.GetProcessesByName(store.Settings.ProcessName).Any(x => x.Id.ToString() == store.Settings.SourceId))
                capture.Stop();
            if (soundpad.Connected && store.Settings.AutoImport)
                foreach (var c in store.Snapshot().Where(x => x.ImportState is "Pending" or "Pending retry" && x.TranscriptionState != "Working" && x.TranscriptionState != "Pending"))
                    pipeline.Enqueue(c, false);
            if (wantMonitoring && !capture.Running)
            {
                AudioSource? source;
                if (store.Settings.SourceKind == "Process")
                    source = CaptureService.Sources().FirstOrDefault(x => x.Kind == "Process" && x.ProcessName == store.Settings.ProcessName);
                else
                    source = CaptureService.Sources().FirstOrDefault(x => x.Kind == store.Settings.SourceKind && x.Id == store.Settings.SourceId);
                if (source is not null)
                {
                    await capture.Start(source, store.Settings.BufferSeconds);
                    store.Settings.SourceId = source.Id;
                    store.Save();
                }
            }
        }
        catch (Exception ex) { store.Log("Connection recovery", ex); notice.Text = ex.Message; }
        finally { checking = false; }
    }
    private void OpenSettings()
    {
        int previousCapacity = store.Settings.BufferSeconds;
        var dialog = new SettingsWindow(store, speech, soundpad, hotkeys) { Owner = this };
        dialog.ShowDialog();
        RefreshSources();
        RefreshMonitors();
        if (wantMonitoring && previousCapacity != store.Settings.BufferSeconds)
            _ = Safe(Start);
    }
    private async Task Exit(int exitCode = 0)
    {
        exiting = true;
        timer.Stop();
        wantMonitoring = false;
        capture.Dispose();
        hotkeys?.Dispose();
        workspace.Dispose();
        tray.Dispose();
        await pipeline.DisposeAsync();
        if (smoke && Path.GetDirectoryName(Path.GetFullPath(store.Root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(store.Root).StartsWith("VoiceReplaySmoke-", StringComparison.Ordinal))
            Directory.Delete(store.Root, true);
        Application.Current.Shutdown(exitCode);
    }
    private async Task Smoke()
    {
        var report = new List<string>();
        try
        {
            store.Settings.AutoImport = false;
            string fixturePath = Path.Combine(store.Root, "fixture.wav");
            if (File.Exists(fixturePath))
                File.Delete(fixturePath);
            var signal = Enumerable.Range(0, 48000).Select(i => (float)(Math.Sin(i * Math.PI * 2 * 440 / 48000) * .5)).ToArray();
            AudioFiles.Write(fixturePath, signal, 48000);
            var fixture = new Clip { Title = "A real waveform, ready to edit", Transcript = "Generated 440 Hz test fixture — no speech", AudioPath = fixturePath, OriginalPath = fixturePath, Duration = 1, TrimEnd = 1, Source = "Generated fixture", TranscriptionState = "Fixture", ImportState = "Disabled" };
            store.Add(fixture);
            RefreshLibrary();
            Screenshot(this, "main.png");
            report.Add($"PASS WPF main window and library loaded; {sources.Items.Count} sources enumerated.");
            var settings = new SettingsWindow(store, speech, soundpad, null) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            settings.Show();
            settings.UpdateLayout();
            Screenshot(settings, "settings.png");
            settings.Close();
            report.Add("PASS Settings window rendered.");
            await workspace.SmokeEdit(fixture);
            Screenshot(this, "editor.png");
            report.Add("PASS Unified inline editor trim/save/undo/revert and mode switches; no popup editor.");
            await capture.Start(new(Environment.ProcessId.ToString(), "Silent smoke-test process", "Process"));
            await workspace.SmokeLive(fixture);
            Screenshot(this, "live.png");
            report.Add($"PASS Actual process capture continues during inline editing and mode changes; live refresh={workspace.LastLiveFps:F1} FPS");
            if (testOutput is not null)
            {
                store.Settings.MonitorDeviceId = testOutput;
                await workspace.SmokePlayback();
                report.Add("PASS Actual WPF Test Playback renders to explicitly selected output; cursor advances; own-process loopback contains test signal.");
            }
            capture.Stop();
            Height = 850;
            Width = 1000;
            await workspace.ShowClip(fixture);
            UpdateLayout();
            Screenshot(this, "compact.png");
            if (library.ActualHeight < 65)
                throw new Exception("Compact editor hides the clip library");
            report.Add("PASS Compact 1000x850 main window retains waveform, library and actions.");
            int dispatched = 0;
            using (var testHotkeys = new Hotkeys(this, id => dispatched = id))
            {
                testHotkeys.Set(new Settings { CaptureHotkey = "Ctrl+Alt+Shift+F23", StopHotkey = "Ctrl+Alt+Shift+F22", EditorHotkey = "Ctrl+Alt+Shift+F21" });
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                NativeSmoke.SendMessage(handle, 0x312, new IntPtr(1), IntPtr.Zero);
                if (dispatched != 1)
                    throw new Exception("Hotkey dispatch failed.");
                using var hidden = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("VoiceReplay hotkey conflict diagnostic"));
                if (NativeSmoke.RegisterHotKey(hidden.Handle, 78, 0x4007, 0x86))
                {
                    NativeSmoke.UnregisterHotKey(hidden.Handle, 78);
                    throw new Exception("Hotkey conflict was not detected.");
                }
            }
            report.Add("PASS Native global hotkey registration, conflict rejection, and own-window message dispatch. No keyboard input generated.");
            report.Add(testOutput is null ? "No audio playback, Soundpad mutation, or audio routing changes were performed." : "Only the explicitly selected low-level test tone was played. No Soundpad mutation, volume/mute/default-device or audio routing changes were performed.");
            report.Add("Runtime loaded from: " + System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            report.Add("Executable: " + Environment.ProcessPath);
        }
        catch (Exception ex) { store.Log("Smoke", ex); report.Add("FAIL " + ex); }
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Smoke"));
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "Smoke", "result.txt"), report);
        await Exit(report.Any(line => line.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0);
    }
    private static void Screenshot(Window window, string filename)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Smoke"));
        using var f = File.Create(Path.Combine(AppContext.BaseDirectory, "Smoke", filename));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        encoder.Save(f);
    }
}
internal static class NativeSmoke
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
