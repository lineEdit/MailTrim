using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MailTrim.Core;

namespace MailTrim.App;

public sealed class WindowPositionManager
{
    private readonly Window window;
    private readonly LocalStore store;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool ready;

    public WindowPositionManager(Window window, LocalStore store)
    {
        this.window = window; this.store = store;
        if (store.Settings.WindowPosition?.IsValid == true) window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SourceInitialized += (_, _) =>
        {
            if (store.Settings.WindowPosition is { IsValid: true } saved) Restore(window, saved);
            ready = true;
        };
        timer.Tick += (_, _) => Save();
        window.LocationChanged += (_, _) => Schedule();
        window.SizeChanged += (_, _) => Schedule();
        window.StateChanged += (_, _) => Schedule();
        window.Closing += (_, _) => Save(); // Includes closing to tray.
        window.Closed += (_, _) => { ready = false; timer.Stop(); };
    }

    private void Schedule()
    {
        if (!ready || !window.IsVisible) return;
        timer.Stop(); timer.Start();
    }
    private void Save()
    {
        timer.Stop();
        if (!ready || Capture(window) is not { IsValid: true } saved || saved == store.Settings.WindowPosition) return;
        var previous = store.Settings.WindowPosition;
        store.Settings.WindowPosition = saved;
        try { store.Save(); } catch { store.Settings.WindowPosition = previous; store.Log("window_position_save_failed"); }
    }
    public static SavedWindowPosition? Capture(Window window)
    {
        var p = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(new WindowInteropHelper(window).Handle, ref p)) return null;
        // WPF's minimized geometry must never replace the normal restore rectangle.
        return new(p.Normal.Left, p.Normal.Top, p.Normal.Right, p.Normal.Bottom,
            window.WindowState == WindowState.Maximized || p.ShowCommand == 3 || (p.ShowCommand == 2 && (p.Flags & 2) != 0));
    }
    public static bool Restore(Window window, SavedWindowPosition saved)
    {
        if (!saved.IsValid) return false;
        var p = new Placement
        {
            Length = Marshal.SizeOf<Placement>(), ShowCommand = saved.Maximized ? 3 : 1,
            Min = new Point { X = -1, Y = -1 }, Max = new Point { X = -1, Y = -1 },
            Normal = new NativeRect { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom }
        };
        // Windows relocates an off-screen placement after monitor/resolution changes.
        // Keep workspace coordinates paired with this API (not SetWindowPos).
        return SetWindowPlacement(new WindowInteropHelper(window).Handle, ref p);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public int Length, Flags, ShowCommand;
        public Point Min, Max;
        public NativeRect Normal;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr handle, ref Placement placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr handle, ref Placement placement);
}
