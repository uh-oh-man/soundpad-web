using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VoiceReplay.Core;

namespace VoiceReplay.App;

public sealed class Hotkeys : IDisposable
{
    private readonly IntPtr handle;
    private readonly HwndSource source;
    private readonly Action<int> trigger;
    private readonly List<int> active = [];
    private bool disposed;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public Hotkeys(Window window, Action<int> trigger)
    {
        handle = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(Hook);
        this.trigger = trigger;
    }
    public void Set(Settings settings)
    {
        if (disposed)
            return;
        Clear();
        try
        {
            int id = 1;
            foreach (string text in new[] { settings.CaptureHotkey, settings.StopHotkey, settings.EditorHotkey })
            {
                var h = HotkeySpec.Parse(text);
                if (!RegisterHotKey(handle, id, h.Modifiers | 0x4000, h.Key))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot register {text}. It may be reserved or used by another app.");
                active.Add(id++);
            }
        }
        catch { Clear(); throw; }
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x312)
        {
            handled = true;
            trigger(w.ToInt32());
        }
        return IntPtr.Zero;
    }
    public void Clear()
    {
        foreach (int id in active)
            UnregisterHotKey(handle, id);
        active.Clear();
    }
    public void Dispose()
    {
        Clear();
        disposed = true;
        source.RemoveHook(Hook);
    }
}
