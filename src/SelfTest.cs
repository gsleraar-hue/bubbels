using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Bubbels
{
    /// <summary>Walks windows through the whole bubble life cycle and logs each state.</summary>
    internal static class SelfTest
    {
        public static void Run(IntPtr[] windows)
        {
            var manager = new BubbleManager();
            manager.Notice += delegate (string text) { Log.Write("TEST notice: " + text); };
            IntPtr a = windows[0], b = windows.Length > 1 ? windows[1] : windows[0];

            var steps = new List<Action>();
            steps.Add(delegate { foreach (IntPtr w in windows) Report("before", w); foreach (IntPtr w in windows) manager.Add(w); });
            steps.Add(delegate { Log.Write("TEST stack: " + manager.Describe()); foreach (IntPtr w in windows) Report("bubbled", w); manager.ExpandFor(a); });
            steps.Add(delegate { Log.Write("TEST row: " + manager.Describe()); Report("open A", a); manager.ExpandFor(b); });
            steps.Add(delegate { Log.Write("TEST row: " + manager.Describe()); Report("A after switch", a); Report("open B", b); manager.CollapseFor(b); });
            steps.Add(delegate { Log.Write("TEST stack: " + manager.Describe()); Report("A folded", a); Report("B folded", b); manager.ReleaseAll(); });
            steps.Add(delegate { foreach (IntPtr w in windows) Report("released", w); Application.ExitThread(); });

            int index = 0;
            var timer = new Timer { Interval = 900 };
            timer.Tick += delegate
            {
                try { steps[index++](); }
                catch (Exception ex) { Log.Exception("TEST", ex); Application.ExitThread(); }
                if (index >= steps.Count) timer.Stop();
            };
            timer.Start();
            Application.Run();
            manager.Dispose();
        }

        private static void Report(string step, IntPtr hwnd)
        {
            Native.RECT frame = Native.GetVisibleFrame(hwnd);
            bool topmost = (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
            Log.Write("TEST {0} [{1}]: visible={2} topmost={3} frame={4},{5} {6}x{7} max={8}",
                      step, Native.GetClass(hwnd), Native.IsWindowVisible(hwnd), topmost,
                      frame.Left, frame.Top, frame.Width, frame.Height, Native.IsZoomed(hwnd));
        }
    }
}
