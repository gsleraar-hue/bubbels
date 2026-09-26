using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Lists top-level windows (optionally only those of processes whose name contains the
// argument), with the details Bubbels cares about. A debugging aid: WinList.exe olk
internal static class WinList
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);

    static void Main(string[] args)
    {
        // "WinList.exe children <hwnd>": the child windows of one window.
        if (args.Length == 2 && args[0] == "children")
        {
            var parent = new IntPtr(long.Parse(args[1]));
            EnumChildWindows(parent, delegate (IntPtr h, IntPtr l)
            {
                var c = new StringBuilder(256); GetClassName(h, c, 256);
                RECT r; GetWindowRect(h, out r);
                Console.WriteLine("  {0,-9} vis={1} parent-chain {2}x{3} [{4}]", h.ToInt64(), IsWindowVisible(h) ? 1 : 0,
                                  r.R - r.L, r.B - r.T, c);
                return true;
            }, IntPtr.Zero);
            return;
        }
        string filter = args.Length > 0 ? args[0].ToLowerInvariant() : null;
        Console.WriteLine("foreground: " + GetForegroundWindow().ToInt64());
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            string name = "?";
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (filter != null && name.ToLowerInvariant().IndexOf(filter) < 0) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            int cloaked; DwmGetWindowAttribute(h, 14, out cloaked, 4);
            RECT r; GetWindowRect(h, out r);
            Console.WriteLine("{0,-9} {1,-14} pid={2,-6} vis={3} cloak={4} owner={5} style={6:X8} ex={7:X8} {8},{9} {10}x{11} [{12}] \"{13}\"",
                h.ToInt64(), name, pid, IsWindowVisible(h) ? 1 : 0, cloaked, GetWindow(h, 4).ToInt64(),
                GetWindowLong(h, -16), GetWindowLong(h, -20), r.L, r.T, r.R - r.L, r.B - r.T, c, t);
            return true;
        }, IntPtr.Zero);
    }
}
