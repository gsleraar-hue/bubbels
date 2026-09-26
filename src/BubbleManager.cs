using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Bubbels
{
    /// <summary>
    /// Turns windows into bubbles and back. All bubbles form one stack against a screen edge,
    /// as on Android: drag it and the others trail behind the one you hold; tap it and they
    /// fan out into a row, with the chosen window opened beside its bubble. Every movement
    /// is a spring, so things overshoot and settle instead of sliding on rails.
    /// </summary>
    internal class BubbleManager : IDisposable
    {
        // Springs: stiffness in 1/s², damping as a ratio (1 = no overshoot).
        private const float SettleStiffness = 380f, SettleDamping = 0.68f;
        private const float TrailStiffness = 900f, TrailDamping = 0.82f;
        private const float CatchStiffness = 700f, CatchDamping = 0.7f;
        private const double RippleSeconds = 0.03;

        private readonly List<Bubble> _bubbles = new List<Bubble>();   // [0] is the top of the stack
        private readonly float _dpiScale;
        private readonly DismissTarget _dismiss;
        private readonly Timer _watchdog = new Timer { Interval = 700 };
        private readonly Timer _physics = new Timer { Interval = 15 };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastStep;
        private readonly Native.WinEventDelegate _foregroundProc;
        private readonly Native.WinEventDelegate _minimizeProc;
        private readonly IntPtr _foregroundHook, _minimizeHook;
        private readonly uint _ownPid = (uint)Process.GetCurrentProcess().Id;
        private ITaskbarList _taskbar;

        // Where the stack rests: the centre of its top bubble, and which edge it hugs.
        private PointF _anchor;
        private bool _onRight = true;
        private bool _open;          // fanned out into a row
        private Bubble _shown;       // whose window is on screen right now
        private bool _working;
        private double _quietUntil;   // focus shuffles right after our own moves are ours, not the user's

        // Dragging.
        private Bubble _leader;
        private bool _dragWhole;
        private bool _caught;
        private Bubble _reopenAfterDrag;
        private readonly List<Bubble> _chain = new List<Bubble>();
        private readonly Queue<KeyValuePair<double, PointF>> _samples = new Queue<KeyValuePair<double, PointF>>();

        public event Action<string> Notice;
        public event EventHandler Changed;

        public BubbleManager()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _dpiScale = g.DpiX / 96f;
            _dismiss = new DismissTarget(_dpiScale);

            _foregroundProc = OnForegroundChanged;
            _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            _minimizeProc = OnMinimizeStart;
            _minimizeHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZESTART,
                IntPtr.Zero, _minimizeProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

            _watchdog.Tick += delegate { Watch(); };
            _watchdog.Start();
            _physics.Tick += delegate { Step(); };

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            _anchor = new PointF(0, area.Top + area.Height / 5f);

            ResetTaskbar();
            RecoverFromCrash();
        }

        public int Count { get { return _bubbles.Count; } }

        public void ResetTaskbar()
        {
            try
            {
                _taskbar = (ITaskbarList)new TaskbarListClass();
                _taskbar.HrInit();
            }
            catch (Exception ex) { _taskbar = null; Log.Exception("taskbar COM", ex); }
        }

        private int Px(float dip) { return (int)Math.Round(dip * _dpiScale); }

        // ------------------------------------------------------- which windows

        private static readonly HashSet<string> ShellClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
            "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "#32768"
        };

        /// <summary>Real application windows: what you would see in Alt+Tab.</summary>
        public bool IsCandidate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
            if (hwnd == Native.GetShellWindow() || Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return false;
            if (Native.GetPid(hwnd) == _ownPid) return false;
            if (ShellClasses.Contains(Native.GetClass(hwnd))) return false;
            if (Native.IsCloaked(hwnd)) return false;

            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            if ((ex & Native.WS_EX_NOACTIVATE) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
            return true;
        }

        public List<IntPtr> ListCandidates()
        {
            var result = new List<IntPtr>();
            Native.EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (IsCandidate(hwnd) && Native.GetText(hwnd).Trim().Length > 0) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public void ExpandFor(IntPtr target) { Bubble b = Find(target); if (b != null) { if (!_open) Open(b); else Show(b); } }
        public void CollapseFor(IntPtr target) { if (_open) Close(); }

        private Bubble Find(IntPtr target)
        {
            foreach (Bubble b in _bubbles) if (b.Target == target) return b;
            return null;
        }

        private bool IsOurs(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && Native.GetPid(hwnd) == _ownPid;
        }

        // ------------------------------------------------------------- hotkey

        /// <summary>The hotkey: bubble the active window, or fold the open one away.</summary>
        public void ToggleForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (_shown != null && (fg == _shown.Target || Native.GetAncestor(fg, Native.GA_ROOTOWNER) == _shown.Target))
            {
                Close();
                return;
            }

            IntPtr root = Native.GetAncestor(fg, Native.GA_ROOT);
            if (root == IntPtr.Zero) root = fg;
            if (!IsCandidate(root))
            {
                Say(Strings.T("The active window cannot become a bubble. Click the window you want to bubble first.",
                              "Het actieve venster kan geen bubbel worden. Klik eerst op het venster dat je wilt bubbelen."));
                return;
            }
            Add(root);
        }

        // ------------------------------------------------------------ add / remove

        public void Add(IntPtr hwnd)
        {
            if (Find(hwnd) != null) return;
            if (!Native.IsWindow(hwnd)) return;
            if (Native.IsHungAppWindow(hwnd))
            {
                Say(Strings.T("That window is not responding; try again in a moment.", "Dat venster reageert niet; probeer het zo nog eens."));
                return;
            }

            var bubble = new Bubble(hwnd, _dpiScale);
            bubble.OriginalPlacement = Native.GetPlacement(hwnd);
            bubble.OriginallyTopmost = (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;
            bubble.PanelSize = InitialPanelSize(hwnd, bubble.OriginalPlacement);

            Native.RECT frame = Native.GetVisibleFrame(hwnd);
            Rectangle windowRect = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);

            Native.ShowWindow(hwnd, Native.SW_HIDE);
            if (Native.IsWindowVisible(hwnd))
            {
                bubble.Dispose();
                Log.Write("could not hide '{0}'", Native.GetText(hwnd));
                Say(Strings.T("This window cannot be hidden. Is it running as administrator? Then Bubbels cannot reach it.",
                              "Dit venster laat zich niet verbergen. Draait het als administrator? Dan kan Bubbels er niet bij."));
                return;
            }

            // The first bubble decides on which screen the stack lives: the window's own.
            if (_bubbles.Count == 0)
            {
                Rectangle area = Screen.FromRectangle(windowRect).WorkingArea;
                _onRight = true;
                _anchor = new PointF(EdgeX(area, true, bubble), area.Top + area.Height / 5f);
            }

            _bubbles.Insert(0, bubble);
            Wire(bubble);
            HookTitle(bubble);

            // It is born where the window was, then flies into the stack.
            float cx = windowRect.Left + windowRect.Width / 2f, cy = windowRect.Top + windowRect.Height / 2f;
            bubble.X = bubble.TX = bubble.NextTX = cx;
            bubble.Y = bubble.TY = bubble.NextTY = cy;
            MoveWindow(bubble);
            bubble.Show();
            bubble.PopIn();

            Layout(false);
            if (_open && _shown != null) Show(_shown);   // the row just shifted
            ApplyZOrder();

            Log.Write("bubbled: '{0}' ({1})", bubble.Title, Native.GetClass(hwnd));
            SaveState();
            OnChanged();
        }

        private void Wire(Bubble bubble)
        {
            bubble.Clicked += delegate { OnClicked(bubble); };
            bubble.ReleaseRequested += delegate { Release(bubble, true); };
            bubble.CloseWindowRequested += delegate { CloseTarget(bubble); };
            bubble.DragStarted += delegate { OnDragStarted(bubble); };
            bubble.DragMoved += delegate (object s, PointEventArgs e) { OnDragMoved(bubble, e.Point); };
            bubble.DragEnded += delegate { OnDragEnded(bubble); };
            bubble.NewsChanged += delegate { UpdateStackDot(bubble); };
        }

        private void HookTitle(Bubble bubble)
        {
            Bubble b = bubble;
            b.NameHookProc = delegate (IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                if (hwnd != b.Target || idObject != Native.OBJID_WINDOW || idChild != 0 || b.IsDisposed) return;
                b.UpdateTitle(Native.GetText(hwnd));
            };
            b.NameHook = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, b.NameHookProc, b.TargetPid, 0, Native.WINEVENT_OUTOFCONTEXT);
        }

        /// <summary>Give the window back exactly as it was before it became a bubble.</summary>
        public void Release(Bubble bubble, bool activate)
        {
            IntPtr hwnd = bubble.Target;
            if (_shown == bubble) _shown = null;

            if (Native.IsWindow(hwnd))
            {
                Native.WINDOWPLACEMENT wp = bubble.OriginalPlacement;
                wp.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
                // Minimised before bubbling? Letting go of a bubble means you want to see it.
                if (wp.showCmd == Native.SW_SHOWMINIMIZED || wp.showCmd == Native.SW_MINIMIZE ||
                    wp.showCmd == Native.SW_SHOWMINNOACTIVE || wp.showCmd == Native.SW_HIDE)
                    wp.showCmd = (wp.flags & Native.WPF_RESTORETOMAXIMIZED) != 0 ? Native.SW_SHOWMAXIMIZED : Native.SW_SHOWNORMAL;
                if (!activate && wp.showCmd == Native.SW_SHOWNORMAL) wp.showCmd = Native.SW_SHOWNOACTIVATE;

                Native.SetWindowPos(hwnd, bubble.OriginallyTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                                    0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Native.SetWindowPlacement(hwnd, ref wp);
                if (!Native.IsWindowVisible(hwnd)) Native.ShowWindow(hwnd, activate ? Native.SW_SHOW : Native.SW_SHOWNOACTIVATE);
                AddTab(hwnd);
                if (activate) Native.SetForegroundWindow(hwnd);
            }

            Log.Write("released: '{0}'", bubble.Title);
            Remove(bubble);
        }

        public void ReleaseAll()
        {
            foreach (Bubble b in _bubbles.ToArray()) Release(b, false);
        }

        private void Remove(Bubble bubble)
        {
            if (_shown == bubble) _shown = null;
            if (_leader == bubble) { _leader = null; _dragWhole = false; _dismiss.Hide(); }
            if (_reopenAfterDrag == bubble) _reopenAfterDrag = null;
            _chain.Remove(bubble);
            _bubbles.Remove(bubble);
            if (bubble.NameHook != IntPtr.Zero) { Native.UnhookWinEvent(bubble.NameHook); bubble.NameHook = IntPtr.Zero; }
            bubble.Close();
            bubble.Dispose();

            if (_bubbles.Count == 0) _open = false;
            else if (_open && _shown == null && _leader == null) _open = false;
            Layout(true);
            ApplyZOrder();
            UpdateStackDot(null);
            SaveState();
            OnChanged();
        }

        private void CloseTarget(Bubble bubble)
        {
            // Show it first: some apps ignore a close request for a hidden window, and if the
            // app asks "save changes?" that question has to be visible.
            if (!_open) Open(bubble); else Show(bubble);
            const uint WM_SYSCOMMAND = 0x0112;
            const int SC_CLOSE = 0xF060;
            IntPtr ignored;
            Native.SendMessageTimeout(bubble.Target, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero, 0x2, 1000, out ignored);
        }

        // ------------------------------------------------------------- layout

        private Rectangle Area { get { return Screen.FromPoint(Point.Round(_anchor)).WorkingArea; } }

        private float EdgeX(Rectangle area, bool right, Bubble sample)
        {
            float r = sample.Diameter / 2f;
            int inset = Px(8);
            return right ? area.Right - inset - r : area.Left + inset + r;
        }

        /// <summary>
        /// Where every bubble should be. Closed: piled on the anchor, the ones below peeking
        /// out a little. Open: a column down the edge, starting at the anchor.
        /// </summary>
        private void Layout(bool ripple)
        {
            if (_bubbles.Count == 0) return;
            Bubble first = _bubbles[0];
            Rectangle area = Area;
            float r = first.Diameter / 2f;
            int inset = Px(8);

            _anchor.X = EdgeX(area, _onRight, first);
            _anchor.Y = Math.Max(area.Top + inset + r, Math.Min(_anchor.Y, area.Bottom - inset - r));

            float step = first.Diameter + Px(12);
            float startY = _anchor.Y;
            if (_open)
            {
                float needed = (_bubbles.Count - 1) * step;
                startY = Math.Max(area.Top + inset + r, Math.Min(startY, area.Bottom - inset - r - needed));
            }

            double now = _clock.Elapsed.TotalSeconds;
            for (int i = 0; i < _bubbles.Count; i++)
            {
                Bubble b = _bubbles[i];
                float tx, ty;
                if (_open)
                {
                    tx = _anchor.X;
                    ty = startY + i * step;
                }
                else
                {
                    // Android shows the second and third bubble just behind the first.
                    int peek = Math.Min(i, 2);
                    tx = _anchor.X + (_onRight ? 1 : -1) * peek * Px(3);
                    ty = _anchor.Y + peek * Px(5);
                }
                b.NextTX = tx;
                b.NextTY = ty;
                b.TargetAt = ripple ? now + i * RippleSeconds : now;
                if (!ripple) { b.TX = tx; b.TY = ty; }
            }
            UpdateStackDot(null);
            StartPhysics();
        }

        /// <summary>Stack order on screen: top of the stack (or the dragged one) above the rest.</summary>
        private void ApplyZOrder()
        {
            var order = new List<Bubble>(_bubbles);
            if (_leader != null) { order.Remove(_leader); order.Insert(0, _leader); }
            for (int i = order.Count - 1; i >= 0; i--) order[i].RaiseToTop();
        }

        private void UpdateStackDot(Bubble changed)
        {
            if (changed != null && !_open && _bubbles.Count > 1 && changed != _bubbles[0] && changed.HasNews)
                _bubbles[0].Pulse();

            for (int i = 0; i < _bubbles.Count; i++)
            {
                bool dot = false;
                if (i == 0 && !_open)
                    for (int j = 1; j < _bubbles.Count; j++) if (_bubbles[j].HasNews) { dot = true; break; }
                _bubbles[i].ShowDot = dot;
            }
        }

        // ------------------------------------------------------------ physics

        private void StartPhysics()
        {
            if (_physics.Enabled) return;
            _lastStep = _clock.Elapsed.TotalSeconds;
            _physics.Start();
        }

        private void Step()
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(1.0 / 20, now - _lastStep);
            _lastStep = now;
            bool moving = false;

            for (int i = 0; i < _bubbles.Count; i++)
            {
                Bubble b = _bubbles[i];
                if (now >= b.TargetAt) { b.TX = b.NextTX; b.TY = b.NextTY; }

                // The bubble under the mouse follows the mouse, unless the X has caught it.
                if (b == _leader && !_caught) { MoveWindow(b); continue; }

                float tx = b.TX, ty = b.TY, k = SettleStiffness, zeta = SettleDamping;
                if (b == _leader && _caught)
                {
                    tx = _dismiss.Center.X; ty = _dismiss.Center.Y; k = CatchStiffness; zeta = CatchDamping;
                }
                else if (_dragWhole)
                {
                    // Each bubble chases the one in front of it: that is the trailing chain.
                    int index = _chain.IndexOf(b);
                    if (index > 0)
                    {
                        Bubble ahead = _chain[index - 1];
                        tx = ahead.X + (_onRight ? 1 : -1) * Px(3);
                        ty = ahead.Y + Px(5);
                        k = TrailStiffness; zeta = TrailDamping;
                    }
                }

                if (Spring(b, tx, ty, k, zeta, dt)) moving = true;
                MoveWindow(b);
            }

            if (!moving && _leader == null) _physics.Stop();
        }

        /// <summary>Semi-implicit Euler in small sub-steps. Returns false once it has come to rest.</summary>
        private static bool Spring(Bubble b, float tx, float ty, float k, float zeta, float dt)
        {
            float c = 2f * zeta * (float)Math.Sqrt(k);
            const float h = 0.004f;
            for (float t = 0; t < dt; t += h)
            {
                float ax = k * (tx - b.X) - c * b.VX;
                float ay = k * (ty - b.Y) - c * b.VY;
                b.VX += ax * h; b.VY += ay * h;
                b.X += b.VX * h; b.Y += b.VY * h;
            }
            if (Math.Abs(tx - b.X) < 0.4f && Math.Abs(ty - b.Y) < 0.4f && Math.Abs(b.VX) < 8 && Math.Abs(b.VY) < 8)
            {
                b.X = tx; b.Y = ty; b.VX = 0; b.VY = 0;
                return b.TX != b.NextTX || b.TY != b.NextTY;
            }
            return true;
        }

        private static void MoveWindow(Bubble b)
        {
            var location = new Point((int)Math.Round(b.X - b.WindowSize / 2f), (int)Math.Round(b.Y - b.WindowSize / 2f));
            if (b.Location != location) b.Location = location;
        }

        // ----------------------------------------------------- open / show / close

        private void OnClicked(Bubble bubble)
        {
            if (!_open) Open(_bubbles[0]);
            else if (bubble == _shown) Close();
            else Show(bubble);
        }

        /// <summary>Fan the stack out into a row and open one window.</summary>
        private void Open(Bubble select)
        {
            _open = true;
            Layout(true);
            ApplyZOrder();
            Show(select);
        }

        /// <summary>Fold everything back into the stack; the last one you looked at goes on top.</summary>
        public void Close()
        {
            if (_shown != null)
            {
                Bubble last = _shown;
                HideWindow(last);
                _shown = null;
                _bubbles.Remove(last);
                _bubbles.Insert(0, last);
            }
            _open = false;
            Layout(true);
            ApplyZOrder();
        }

        /// <summary>Show this bubble's window next to its place in the row.</summary>
        private void Show(Bubble bubble)
        {
            IntPtr hwnd = bubble.Target;
            if (!Native.IsWindow(hwnd)) { Remove(bubble); return; }

            Bubble previous = _shown;
            _working = true;
            try
            {
                // Aim at where the bubble is going, not where it is mid-flight.
                float r = bubble.Diameter / 2f;
                var slot = new Rectangle((int)(bubble.NextTX - r), (int)(bubble.NextTY - r), bubble.Diameter, bubble.Diameter);
                Rectangle area = Area;
                int gap = Px(12);

                int w = Math.Min(bubble.PanelSize.Width, area.Width - slot.Width - 3 * gap);
                int h = Math.Min(bubble.PanelSize.Height, area.Height - 2 * gap);

                // Dialogs with a fixed size keep it; stretching them only leaves empty space.
                const int WS_THICKFRAME = 0x00040000;
                if ((Native.GetWindowLong(hwnd, Native.GWL_STYLE) & WS_THICKFRAME) == 0)
                {
                    Native.RECT own = bubble.OriginalPlacement.rcNormalPosition;
                    w = own.Width;
                    h = own.Height;
                }
                int anchorX = _onRight ? slot.Left - gap : slot.Right + gap;
                int x = _onRight ? anchorX - w : anchorX;
                int y = Math.Max(area.Top + gap, Math.Min(slot.Top, area.Bottom - gap - h));

                // A maximised or minimised window has to become a normal one first, or it would
                // ignore the size we give it. Doing that through the placement avoids a flash.
                if (Native.IsZoomed(hwnd) || Native.IsIconic(hwnd))
                {
                    Native.WINDOWPLACEMENT wp = Native.GetPlacement(hwnd);
                    wp.showCmd = Native.SW_SHOWNOACTIVATE;
                    wp.flags = 0;
                    // The placement speaks in workspace coordinates: relative to the primary
                    // screen's work area rather than to the screen itself.
                    Rectangle ws = Screen.PrimaryScreen.WorkingArea;
                    wp.rcNormalPosition = new Native.RECT
                    {
                        Left = x - ws.Left, Top = y - ws.Top, Right = x + w - ws.Left, Bottom = y + h - ws.Top
                    };
                    Native.SetWindowPlacement(hwnd, ref wp);
                }

                _shown = bubble;
                bubble.SetExpanded(true);
                // With bubbles always on top, the open window floats too, so the pair stays
                // together; otherwise it is an ordinary window brought to the front.
                Native.SetWindowPos(hwnd, Bubble.AlwaysOnTop ? Native.HWND_TOPMOST : IntPtr.Zero,
                                    x, y, w, h, Native.SWP_SHOWWINDOW);

                // Windows 10/11 windows have invisible resize borders; line up what you can see.
                Native.RECT frame = Native.GetVisibleFrame(hwnd);
                int dx = _onRight ? anchorX - frame.Right : anchorX - frame.Left;
                int dy = y - frame.Top;
                if (dx != 0 || dy != 0)
                    Native.SetWindowPos(hwnd, IntPtr.Zero, x + dx, y + dy, 0, 0,
                                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

                DeleteTab(hwnd);
                Native.SetForegroundWindow(hwnd);

                // Hide the previous window only now: hiding it first would hand the focus to
                // some unrelated window in between.
                if (previous != null && previous != bubble) HideWindow(previous);
                ApplyZOrder();
            }
            finally { _working = false; _quietUntil = _clock.Elapsed.TotalSeconds + 0.4; }
        }

        private void HideWindow(Bubble bubble)
        {
            IntPtr hwnd = bubble.Target;
            _working = true;
            try
            {
                if (Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && !Native.IsZoomed(hwnd))
                {
                    // You may have resized it while it was open; next time it opens at that size.
                    Native.RECT rect;
                    if (Native.GetWindowRect(hwnd, out rect) && rect.Width > 100 && rect.Height > 100)
                        bubble.PanelSize = new Size(rect.Width, rect.Height);
                }
                Native.ShowWindow(hwnd, Native.SW_HIDE);
                Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            finally { _working = false; _quietUntil = _clock.Elapsed.TotalSeconds + 0.4; }
            bubble.SetExpanded(false);
        }

        private Size InitialPanelSize(IntPtr hwnd, Native.WINDOWPLACEMENT wp)
        {
            Rectangle area = Screen.FromHandle(hwnd).WorkingArea;
            int w = wp.rcNormalPosition.Width, h = wp.rcNormalPosition.Height;
            int minW = Px(420), minH = Px(480);
            w = Math.Max(Math.Min(minW, area.Width), Math.Min(w, (int)(area.Width * 0.55)));
            h = Math.Max(Math.Min(minH, area.Height), Math.Min(h, (int)(area.Height * 0.85)));
            return new Size(w, h);
        }

        // --------------------------------------------------------------- dragging

        private void OnDragStarted(Bubble bubble)
        {
            _leader = bubble;
            _caught = false;
            _samples.Clear();
            bubble.VX = bubble.VY = 0;

            if (!_open)
            {
                // Closed: you drag the whole stack, the others in a chain behind this one.
                _dragWhole = true;
                _chain.Clear();
                _chain.Add(bubble);
                foreach (Bubble b in _bubbles) if (b != bubble) _chain.Add(b);
            }
            else
            {
                // Open: you pull one bubble out of the row; its window steps aside meanwhile.
                _dragWhole = false;
                _reopenAfterDrag = _shown;
                if (_shown != null) { HideWindow(_shown); _shown = null; }
            }

            _dismiss.ShowOn(Screen.FromPoint(Point.Round(new PointF(bubble.X, bubble.Y))));
            ApplyZOrder();
            StartPhysics();
        }

        private void OnDragMoved(Bubble bubble, Point desiredTopLeft)
        {
            if (bubble != _leader) return;
            var center = new PointF(desiredTopLeft.X + bubble.WindowSize / 2f, desiredTopLeft.Y + bubble.WindowSize / 2f);

            double now = _clock.Elapsed.TotalSeconds;
            _samples.Enqueue(new KeyValuePair<double, PointF>(now, center));
            while (_samples.Count > 2 && now - _samples.Peek().Key > 0.1) _samples.Dequeue();

            Screen screen = Screen.FromPoint(Cursor.Position);
            if (!screen.WorkingArea.Contains(_dismiss.Center)) _dismiss.ShowOn(screen);

            Point target = _dismiss.Center;
            double distance = Math.Sqrt(Math.Pow(center.X - target.X, 2) + Math.Pow(center.Y - target.Y, 2));
            bool caught = distance < _dismiss.CatchRadius;

            if (caught != _caught)
            {
                _caught = caught;
                _dismiss.Hot = caught;
            }
            if (!caught)
            {
                PointF velocity = Velocity();
                bubble.X = center.X; bubble.Y = center.Y;
                bubble.VX = velocity.X; bubble.VY = velocity.Y;
            }
            StartPhysics();
        }

        private PointF Velocity()
        {
            if (_samples.Count < 2) return PointF.Empty;
            KeyValuePair<double, PointF> first = _samples.Peek(), last = first;
            foreach (KeyValuePair<double, PointF> s in _samples) last = s;
            double span = last.Key - first.Key;
            if (span < 0.005) return PointF.Empty;
            return new PointF((float)((last.Value.X - first.Value.X) / span), (float)((last.Value.Y - first.Value.Y) / span));
        }

        private void OnDragEnded(Bubble bubble)
        {
            if (bubble != _leader) return;
            bool release = _caught;
            bool whole = _dragWhole;
            PointF velocity = Velocity();

            _dismiss.Hide();
            _leader = null;
            _dragWhole = false;
            _caught = false;

            if (release)
            {
                if (whole) { Log.Write("whole stack released"); ReleaseAll(); }
                else Release(bubble, true);
                return;
            }

            if (whole)
            {
                // A flick carries on: aim where the stack would coast to, then pick that edge.
                Rectangle area = Screen.FromPoint(Point.Round(new PointF(bubble.X, bubble.Y))).WorkingArea;
                float projectedX = bubble.X + velocity.X * 0.2f;
                float projectedY = bubble.Y + velocity.Y * 0.2f;
                _onRight = projectedX > area.Left + area.Width / 2f;
                _anchor = new PointF(EdgeX(area, _onRight, bubble), projectedY);
                bubble.VX = velocity.X; bubble.VY = velocity.Y;
                Layout(false);
            }
            else
            {
                Layout(false);
                Bubble reopen = _reopenAfterDrag;
                _reopenAfterDrag = null;
                if (reopen != null && _bubbles.Contains(reopen)) Show(reopen);
                else Close();
            }
            ApplyZOrder();
        }

        /// <summary>After the always-on-top setting changed.</summary>
        public void ApplyTopmost()
        {
            ApplyZOrder();
            if (_shown != null && Native.IsWindow(_shown.Target))
                Native.SetWindowPos(_shown.Target, Bubble.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                                    0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void KeepOnScreen()
        {
            Layout(false);
        }

        public void Rerender()
        {
            Theme.Refresh();
            foreach (Bubble b in _bubbles) b.Render();
        }

        // ------------------------------------------------------------ watching

        private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // Another app may just have made itself topmost; climb back over it.
            if (Bubble.AlwaysOnTop && _leader == null) ApplyZOrder();

            Bubble open = _shown;
            if (open == null || _working || _leader != null) return;
            if (_clock.Elapsed.TotalSeconds < _quietUntil) return;

            // The event can arrive late; what counts is who has the focus now.
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == open.Target || IsOurs(fg)) return;
            if (Native.GetAncestor(fg, Native.GA_ROOTOWNER) == open.Target) return;

            // Menus, dropdowns and pickers of the same app are part of it, not "elsewhere".
            if (Native.GetPid(fg) == open.TargetPid && !IsCandidate(fg)) return;

            Close();
        }

        private void OnMinimizeStart(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // The minimise button of an open bubble means "fold away".
            if (_shown != null && hwnd == _shown.Target && !_working) Close();
        }

        /// <summary>Called for each shell hook message (window gone, window asks for attention).</summary>
        public void OnShellMessage(int code, IntPtr hwnd)
        {
            Bubble b = Find(hwnd);
            if (b == null) return;
            if (code == Native.HSHELL_FLASH) b.SetAttention();
            else if (code == Native.HSHELL_WINDOWDESTROYED && !Native.IsWindow(hwnd)) Remove(b);
        }

        private void Watch()
        {
            if (_working || _leader != null) return;
            if (Bubble.AlwaysOnTop) ApplyZOrder();
            foreach (Bubble b in _bubbles.ToArray())
            {
                IntPtr hwnd = b.Target;
                if (!Native.IsWindow(hwnd))
                {
                    Log.Write("window gone: '{0}'", b.Title);
                    Remove(b);
                }
                else if (b != _shown && Native.IsWindowVisible(hwnd))
                {
                    // The app brought itself back (tray icon, a notification click). Respect that.
                    Log.Write("window came back by itself: '{0}'", b.Title);
                    Native.SetWindowPos(hwnd, b.OriginallyTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                                        0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    AddTab(hwnd);
                    Remove(b);
                }
                else if (b == _shown && (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)))
                {
                    Close();
                }
            }
        }

        // ------------------------------------------------------------- taskbar

        private void DeleteTab(IntPtr hwnd)
        {
            try { if (_taskbar != null) _taskbar.DeleteTab(hwnd); } catch { }
        }

        private void AddTab(IntPtr hwnd)
        {
            try { if (_taskbar != null) _taskbar.AddTab(hwnd); } catch { }
        }

        // ---------------------------------------------------------- crash safety

        private static string StatePath { get { return Path.Combine(Program.DataFolder, "bubbels.state"); } }

        /// <summary>
        /// Hidden windows are invisible for good if this process dies without giving them
        /// back. So every bubble is written down, and the next start shows them again.
        /// </summary>
        private void SaveState()
        {
            try
            {
                if (_bubbles.Count == 0) { if (File.Exists(StatePath)) File.Delete(StatePath); return; }
                var text = new StringBuilder();
                foreach (Bubble b in _bubbles)
                {
                    Native.WINDOWPLACEMENT wp = b.OriginalPlacement;
                    text.AppendFormat(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}\t{4},{5},{6},{7}\t{8}\r\n",
                        b.Target.ToInt64(), b.TargetPid, wp.showCmd, wp.flags,
                        wp.rcNormalPosition.Left, wp.rcNormalPosition.Top, wp.rcNormalPosition.Right, wp.rcNormalPosition.Bottom,
                        b.OriginallyTopmost ? 1 : 0);
                }
                Directory.CreateDirectory(Program.DataFolder);
                File.WriteAllText(StatePath, text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Exception("saving state", ex); }
        }

        private void RecoverFromCrash()
        {
            if (!File.Exists(StatePath)) return;
            try
            {
                int restored = 0;
                foreach (string line in File.ReadAllLines(StatePath, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 6) continue;
                    var hwnd = new IntPtr(long.Parse(parts[0], CultureInfo.InvariantCulture));
                    uint pid = uint.Parse(parts[1], CultureInfo.InvariantCulture);
                    // A window handle can be reused by another process after a restart; only
                    // touch it if it still belongs to the same process and is still hidden.
                    if (!Native.IsWindow(hwnd) || Native.GetPid(hwnd) != pid || Native.IsWindowVisible(hwnd)) continue;

                    string[] r = parts[4].Split(',');
                    var wp = Native.GetPlacement(hwnd);
                    wp.flags = int.Parse(parts[3], CultureInfo.InvariantCulture);
                    wp.showCmd = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    if (wp.showCmd == Native.SW_HIDE || wp.showCmd == Native.SW_SHOWMINIMIZED || wp.showCmd == Native.SW_MINIMIZE)
                        wp.showCmd = Native.SW_SHOWNOACTIVATE;
                    wp.rcNormalPosition = new Native.RECT
                    {
                        Left = int.Parse(r[0], CultureInfo.InvariantCulture), Top = int.Parse(r[1], CultureInfo.InvariantCulture),
                        Right = int.Parse(r[2], CultureInfo.InvariantCulture), Bottom = int.Parse(r[3], CultureInfo.InvariantCulture)
                    };
                    Native.SetWindowPos(hwnd, parts[5] == "1" ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    Native.SetWindowPlacement(hwnd, ref wp);
                    if (!Native.IsWindowVisible(hwnd)) Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
                    AddTab(hwnd);
                    restored++;
                }
                File.Delete(StatePath);
                if (restored > 0)
                {
                    Log.Write("restored {0} hidden window(s) from a previous session", restored);
                    Say(restored == 1
                        ? Strings.T("A window that was still in a bubble has been restored.",
                                    "Een venster dat nog in een bubbel zat is teruggezet.")
                        : Strings.T(restored + " windows that were still in bubbles have been restored.",
                                    restored + " vensters die nog in een bubbel zaten zijn teruggezet."));
                }
            }
            catch (Exception ex) { Log.Exception("recovering after crash", ex); }
        }

        // --------------------------------------------------------------- misc

        private void Say(string text)
        {
            if (Notice != null) Notice(text);
        }

        private void OnChanged()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        /// <summary>For the self-test: where each bubble is and where it is heading.</summary>
        public string Describe()
        {
            var text = new StringBuilder(_open ? "open" : "closed");
            foreach (Bubble b in _bubbles)
                text.AppendFormat(CultureInfo.InvariantCulture, " [{0:0},{1:0}->{2:0},{3:0}{4}]",
                                  b.X, b.Y, b.NextTX, b.NextTY, b == _shown ? " open" : "");
            return text.ToString();
        }

        public void Dispose()
        {
            _watchdog.Stop();
            _physics.Stop();
            ReleaseAll();
            if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
            if (_minimizeHook != IntPtr.Zero) Native.UnhookWinEvent(_minimizeHook);
            _dismiss.Dispose();
        }
    }
}
