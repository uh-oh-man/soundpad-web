using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
namespace SoundpadWeb;

/// <summary>
/// The UI thread of the desktop companion (2.36). Program.cs runs the message loop on the STA main thread; the
/// companion itself runs on the thread pool and reaches the UI only through this class.
/// </summary>
internal sealed class DesktopUi : IDisposable
{
    private readonly Control marshal = new();
    private CompanionWindow? window;
    public ApplicationContext Context { get; } = new();
    /// <summary>The data folder the window's WebView2 profile lives in, once a window was opened.</summary>
    public string? DataFolder { get; private set; }
    /// <summary>Created on the UI thread, before its message loop starts.</summary>
    public DesktopUi() { marshal.CreateControl(); _ = marshal.Handle; }

    private Task<T> OnUi<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        marshal.BeginInvoke(() => { try { done.SetResult(work()); } catch (Exception ex) { done.SetException(ex); } });
        return done.Task;
    }

    /// <summary>First launch without a built-in website and no console: asks in a small dialog instead of the console prompt.</summary>
    public Task<string?> AskWebsite() => OnUi(WebsiteDialog.Ask);

    public Task ShowError(string message) => OnUi(() => MessageBox.Show(message, "Soundpad Web", MessageBoxButtons.OK, MessageBoxIcon.Error));

    /// <summary>Opens the companion window on <paramref name="url"/>. <paramref name="failed"/> runs (on the UI thread)
    /// when WebView2 cannot run, after the window is gone; <paramref name="closing"/> runs when the person closes it.</summary>
    /// <param name="asking">Asked when the person closes the window (2.40): true when the page was asked first (the quit
    /// prompt), so the window stays open until it answers.</param>
    public Task Open(string url, string origin, string directory, Action<string> failed, Action<bool> closing, bool hidden = false, Func<bool>? asking = null) => OnUi(() =>
    {
        window = new CompanionWindow(url, origin, directory) { AskBeforeClosing = asking };
        DataFolder = directory;
        window.Failed += failed;
        window.Quitting += closing;
        // --hidden-window (automated checks only): the window and its WebView2 run but are never shown, so a check
        // never takes the screen or keyboard focus from whatever the person is doing, such as a game.
        if (hidden) _ = window.Handle; else window.Show();
        return true;
    });

    /// <summary>Ends the message loop once the companion has finished its cleanup.</summary>
    public void Quit() { try { marshal.BeginInvoke(() => { window?.CloseForExit(); Context.ExitThread(); }); } catch (InvalidOperationException) { } }

    /// <summary>After the window closed: once WebView2's browser has let go (usually within a second or two), its
    /// copies of this launch's link go too, so the owner key and token do not stay on disk after quitting.</summary>
    public void ForgetLaunch(string directory)
    {
        for (var deadline = Environment.TickCount64 + 3000; !CompanionWindow.Forget(directory) && Environment.TickCount64 < deadline;) Thread.Sleep(100);
    }

    public void Dispose() { window?.Dispose(); marshal.Dispose(); Context.Dispose(); }
}

/// <summary>The Soundpad Web window (2.36): one WebView2 showing the website as the session owner.</summary>
internal sealed class CompanionWindow : Form
{
    private readonly string url, origin, directory, placementPath;
    private CoreWebView2Environment? environment;
    private CoreWebView2Controller? controller;
    private readonly Queue<long> browserRestarts = new();
    private bool failing, exiting;
    private int loadFailures;
    private System.Windows.Forms.Timer? retry;
    private void Retry(object? sender, EventArgs e) { retry?.Stop(); try { controller?.CoreWebView2.Navigate(url); } catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { } }
    public event Action<string>? Failed;
    /// <summary>The person closed the window (true when Windows is ending the session). The companion quits.</summary>
    public event Action<bool>? Quitting;
    /// <summary>True when closing must wait for the page's answer (the quit prompt, 2.40).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? AskBeforeClosing { get; init; }
    // Charcoal, close to the website's background, so the window never flashes white while the page loads.
    private static readonly Color Charcoal = Color.FromArgb(0x17, 0x18, 0x1b);

    public CompanionWindow(string url, string origin, string directory)
    {
        this.url = url; this.origin = origin; this.directory = directory;
        placementPath = Path.Combine(directory, "window.json");
        Text = "Soundpad Web";
        BackColor = Charcoal;
        MinimumSize = new Size(560, 420);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception ex) when (ex is ArgumentException or IOException) { }
        WindowPlacement.Load(placementPath).Apply(this);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); BeginInvoke(StartWebView); }

    private async void StartWebView()
    {
        try
        {
            // Its own profile in the data folder, so it never shares cookies or storage with the person's Edge.
            if (environment == null) ForgetLastLaunch();
            environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(directory, "webview2"));
            if (IsDisposed || exiting) return;
            controller = await environment.CreateCoreWebView2ControllerAsync(Handle);
            if (IsDisposed || exiting) { controller.Close(); return; }
            controller.DefaultBackgroundColor = Charcoal;
            controller.Bounds = ClientRectangle;
            var core = controller.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            // The window shows the website and nothing else: other pages open in the default browser.
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
            core.NavigationStarting += (_, e) => { if (!SameSite(e.Uri)) { e.Cancel = true; OpenOutside(e.Uri); } };
            // Soundpad Web never records from a microphone or camera (only a chosen output, by the companion).
            core.PermissionRequested += (_, e) => { if (e.PermissionKind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera) e.State = CoreWebView2PermissionState.Deny; };
            core.ProcessFailed += OnProcessFailed;
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    loadFailures = 0;
                    // Chromium records the launch link (port, token, owner key) in its history; the window needs none.
                    _ = ClearHistory(core);
                    return;
                }
                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                // Usually no internet yet (the website is online): say so once and keep trying quietly.
                if (loadFailures++ == 0) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] The website did not load in the window ({e.WebErrorStatus}); retrying every 10 seconds.");
                retry ??= new System.Windows.Forms.Timer { Interval = 10000 };
                retry.Tick -= Retry; retry.Tick += Retry; retry.Start();
            };
            // The page keeps running while minimized (proved for 2.36): the controller stays visible, so Chromium
            // does not throttle the host session's timers or WebRTC.
            controller.IsVisible = true;
            core.Navigate(url);
            if (ContainsFocus) controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        // Whatever stops WebView2 from starting, the companion carries on without its window (the 2.35 way).
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex is WebView2RuntimeNotFoundException ? WebViewRuntime.Missing : "The Soundpad Web window could not start (" + ex.Message.Trim() + ").");
        }
    }

    private static async Task ClearHistory(CoreWebView2 core)
    {
        try { await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    /// <summary>
    /// The page keeps its launch link in sessionStorage (for reloads) and Chromium writes both that and its history to
    /// the profile. Neither outlives the launch it belongs to, so the previous launch's copies (its token and owner
    /// key, worthless now) are removed before WebView2 opens the profile. Local storage, the person's own website
    /// settings, is left alone.
    /// </summary>
    private void ForgetLastLaunch() => Forget(directory);

    /// <summary>Removes the launch links WebView2 kept (see ForgetLastLaunch). False while its browser still holds them.</summary>
    public static bool Forget(string directory)
    {
        var profile = Path.Combine(directory, "webview2", "EBWebView", "Default");
        try
        {
            if (Directory.Exists(Path.Combine(profile, "Session Storage"))) Directory.Delete(Path.Combine(profile, "Session Storage"), true);
            if (Directory.Exists(Path.Combine(profile, "Sessions"))) Directory.Delete(Path.Combine(profile, "Sessions"), true);
            foreach (var file in Directory.Exists(profile) ? Directory.GetFiles(profile, "History*") : []) File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; /* still in use: removed next launch */ }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] The window's web content stopped ({e.ProcessFailedKind}); reloading.");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The whole browser went away: start a new one, but not in a loop.
            var now = Environment.TickCount64;
            browserRestarts.Enqueue(now);
            while (browserRestarts.Peek() < now - 60000) browserRestarts.Dequeue();
            try { controller?.Close(); } catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
            controller = null; environment = null;
            if (browserRestarts.Count > 3) { Fail("The Soundpad Web window's browser keeps stopping."); return; }
            BeginInvoke(StartWebView);
        }
        else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // The page reconnects as owner by itself and gets its session identity back from the companion.
            try { controller?.CoreWebView2.Reload(); } catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
        }
    }

    private void Fail(string reason)
    {
        if (failing || exiting) return;
        failing = true;
        try { controller?.Close(); } catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
        controller = null;
        Close();
        Failed?.Invoke(reason);
    }

    private bool SameSite(string address) => Uri.TryCreate(address, UriKind.Absolute, out var target) && string.Equals(target.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase);

    private static void OpenOutside(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var target) || target.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Minimizing leaves the page as it is (and visible to Chromium); only real sizes reach it.
        if (controller != null && WindowState != FormWindowState.Minimized && ClientSize.Width > 0 && ClientSize.Height > 0) controller.Bounds = ClientRectangle;
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); try { controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { } }

    protected override void OnMove(EventArgs e) { base.OnMove(e); try { controller?.NotifyParentWindowPositionChanged(); } catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { } }

    /// <summary>Called when the companion has already stopped (Enter, Ctrl+C): closes without asking it to quit again.</summary>
    public void CloseForExit() { exiting = true; if (!IsDisposed) Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        // Clips in Temp would be removed (2.40): the page asks first, and quits through the companion when told to.
        if (!failing && !exiting && e.CloseReason == CloseReason.UserClosing && AskBeforeClosing?.Invoke() == true)
        {
            e.Cancel = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            return;
        }
        WindowPlacement.From(this).Save(placementPath);
        Hide();
        retry?.Dispose(); retry = null;
        try { controller?.Close(); } catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
        controller = null;
        if (!failing && !exiting) { exiting = true; Quitting?.Invoke(e.CloseReason == CloseReason.WindowsShutDown); }
    }
}

/// <summary>Where the window was, in its own small file (never state.json, 2.36).</summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    public static WindowPlacement Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4096) return Default();
            var saved = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path), Json.Options);
            // Only a size that still fits a connected screen (monitors change between launches).
            if (saved == null || saved.Width is < 400 or > 20000 || saved.Height is < 300 or > 20000) return Default();
            var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
            if (!Screen.AllScreens.Any(s => { var shared = Rectangle.Intersect(s.WorkingArea, bounds); return shared.Width >= 200 && shared.Height >= 100; })) return Default();
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Default(); }
    }

    private static WindowPlacement Default()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
        int width = Math.Min(1360, area.Width - 80), height = Math.Min(880, area.Height - 80);
        return new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height, false);
    }

    public void Apply(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(X, Y, Width, Height);
        if (Maximized) form.WindowState = FormWindowState.Maximized;
    }

    public static WindowPlacement From(Form form)
    {
        var normal = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        return new(normal.X, normal.Y, normal.Width, normal.Height, form.WindowState == FormWindowState.Maximized);
    }

    public void Save(string path)
    {
        try { var temporary = path + ".tmp"; File.WriteAllText(temporary, Json.Write(this)); File.Move(temporary, path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next launch uses the default place */ }
    }
}

internal static class WebViewRuntime
{
    public const string Missing = "The Soundpad Web window needs the Microsoft Edge WebView2 Runtime, which is not installed. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 (Microsoft's Evergreen bootstrapper) and start Soundpad Web again.";

    /// <summary>The installed WebView2 Runtime version, or null when there is none.</summary>
    public static string? Version()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or DllNotFoundException or BadImageFormatException) { return null; }
    }
}

/// <summary>The first-launch website question as a dialog, for a companion started without a console (2.36).</summary>
internal static class WebsiteDialog
{
    public static string? Ask()
    {
        using var form = new Form { Text = "Soundpad Web — first launch", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(520, 132), AutoScaleMode = AutoScaleMode.Font };
        var label = new Label { Text = "Paste your published HTTPS website URL (saved for future launches):", Left = 12, Top = 12, Width = 496, AutoSize = false, Height = 20 };
        var input = new TextBox { Left = 12, Top = 40, Width = 496, AccessibleName = "Website URL" };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 332, Top = 88, Width = 84 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 424, Top = 88, Width = 84 };
        form.Controls.AddRange([label, input, ok, cancel]);
        form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }
}
