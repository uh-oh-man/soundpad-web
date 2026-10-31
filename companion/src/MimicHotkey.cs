using System.ComponentModel;
using System.Runtime.InteropServices;
namespace SoundpadWeb;

/// <summary>A system-wide hotkey. Tests use a fixture; production registers F6 with Windows.</summary>
public interface IGlobalHotkey : IDisposable
{
    string Name { get; }
    bool Active { get; }
    /// <summary>Registers the key; <paramref name="pressed"/> runs on a background thread for each press.</summary>
    void Register(Action pressed);
    void Unregister();
}

/// <summary>
/// F6 anywhere on the host PC, as in Voice Replay Studio. The companion has no window, so a dedicated
/// thread owns the registration and receives WM_HOTKEY in its own message queue.
/// </summary>
public sealed class WindowsHotkey(uint key = 0x75, string name = "F6") : IGlobalHotkey
{
    private const int WmHotkey = 0x0312, WmQuit = 0x0012, ModNoRepeat = 0x4000;
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public IntPtr Window; public uint Id; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private readonly object gate = new();
    private Thread? thread;
    private uint threadId;
    public string Name { get; } = name;
    public bool Active { get { lock (gate) return thread != null; } }

    public void Register(Action pressed)
    {
        lock (gate)
        {
            if (thread != null) return;
            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // create this thread's message queue
                threadId = GetCurrentThreadId();
                if (!RegisterHotKey(IntPtr.Zero, 1, ModNoRepeat, key)) { ready.TrySetResult(Marshal.GetLastWin32Error()); return; }
                ready.TrySetResult(0);
                try
                {
                    while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                        if (message.Id == WmHotkey) ThreadPool.QueueUserWorkItem(_ => pressed());
                }
                finally { UnregisterHotKey(IntPtr.Zero, 1); }
            }) { IsBackground = true, Name = "Mimic hotkey" };
            worker.Start();
            int error = ready.Task.GetAwaiter().GetResult();
            if (error != 0)
            {
                worker.Join();
                throw new InvalidDataException($"{Name} is already used by another app (for example Voice Replay Studio). Close it or keep using the Mimic window. ({new Win32Exception(error).Message})");
            }
            thread = worker;
        }
    }

    public void Unregister()
    {
        Thread? old; uint id;
        lock (gate) { old = thread; thread = null; id = threadId; }
        if (old == null) return;
        PostThreadMessage(id, WmQuit, IntPtr.Zero, IntPtr.Zero);
        old.Join(TimeSpan.FromSeconds(2));
    }
    public void Dispose() => Unregister();
}
