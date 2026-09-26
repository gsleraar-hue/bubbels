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
            steps.Add(delegate { Log.Write("TEST row: " + manager.Describe()); Report("A after switch", a); Report("open B", b); manager.ExpandFor(a); });
            steps.Add(delegate { Report("A again", a); Report("B after switch", b); manager.CollapseFor(a); });
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
            Log.Write("TEST {0} [{1}]: visible={2} topmost={3} frame={4},{5} {6}x{7} max={8} content={9}",
                      step, Native.GetClass(hwnd), Native.IsWindowVisible(hwnd), topmost,
                      frame.Left, frame.Top, frame.Width, frame.Height, Native.IsZoomed(hwnd), LargestChild(hwnd));
        }

        /// <summary>
        /// The size of the biggest visible child window. For WebView2/Chromium apps that is the
        /// web content: if it does not follow the window size, the app stopped drawing.
        /// </summary>
        private static string LargestChild(IntPtr hwnd)
        {
            int bestW = 0, bestH = 0;
            Native.EnumChildWindows(hwnd, delegate (IntPtr child, IntPtr l)
            {
                Native.RECT r;
                if (Native.IsWindowVisible(child) && Native.GetWindowRect(child, out r) && r.Width * r.Height > bestW * bestH)
                { bestW = r.Width; bestH = r.Height; }
                return true;
            }, IntPtr.Zero);
            return bestW + "x" + bestH;
        }
    }
}
