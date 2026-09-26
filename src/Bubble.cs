using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Bubbels
{
    /// <summary>
    /// One floating bubble: a per-pixel-alpha window that never takes focus. It knows how to
    /// draw itself and how to turn raw mouse input into click / drag events; what those mean
    /// for the window it stands for is decided by <see cref="BubbleManager"/>.
    /// </summary>
    internal class Bubble : Form
    {
        public readonly IntPtr Target;
        public readonly uint TargetPid;
        public Native.WINDOWPLACEMENT OriginalPlacement;
        public bool OriginallyTopmost;
        public Size PanelSize;
        public bool Expanded;
        public IntPtr NameHook;
        public Native.WinEventDelegate NameHookProc;   // kept here so the GC leaves it alone

        // Spring physics, driven by the manager: centre, velocity (px/s) and where it is heading.
        // A new target can be scheduled for later (TargetAt) so a group moves in a ripple.
        public float X, Y, VX, VY, TX, TY, NextTX, NextTY;
        public double TargetAt;

        public event EventHandler Clicked;
        public event EventHandler NewsChanged;
        public event EventHandler DragStarted;
        public event EventHandler<PointEventArgs> DragMoved;
        public event EventHandler DragEnded;
        public event EventHandler ReleaseRequested;
        public event EventHandler CloseWindowRequested;

        public readonly int Diameter;
        public readonly int Inset;
        public int WindowSize { get { return Diameter + 2 * Inset; } }

        private readonly Bitmap _icon;
        private readonly ToolTip _tip = new ToolTip { ShowAlways = true, InitialDelay = 400 };
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _toggleItem;
        private string _title = "";
        private int _badge;
        private bool _attention;

        // Animation state.
        private float _scale = 1f;
        private float _pulse = -1f;
        private readonly Timer _animTimer = new Timer { Interval = 15 };
        private readonly Stopwatch _popClock = new Stopwatch();
        private readonly Stopwatch _pulseClock = new Stopwatch();
        private bool _popping;
        private bool _showDot;
        private int _pulsesLeft;

        // Mouse state.
        private bool _down, _dragging;
        private Point _downCursor, _downLocation;

        public Bubble(IntPtr target, float dpiScale)
        {
            Target = target;
            TargetPid = Native.GetPid(target);
            Diameter = (int)Math.Round(56 * dpiScale);
            Inset = (int)Math.Round(18 * dpiScale);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(WindowSize, WindowSize);
            Text = "Bubble";

            _icon = WindowIcons.Get(target, (int)(Diameter * 0.6f));

            Modern.Apply(_menu);
            _toggleItem = new ToolStripMenuItem(Strings.T("Open", "Openen"), null, delegate { Raise(Clicked); });
            _menu.Items.Add(_toggleItem);
            _menu.Items.Add(new ToolStripMenuItem(Strings.T("Turn back into a normal window", "Terugzetten als gewoon venster"), null, delegate { Raise(ReleaseRequested); }));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem(Strings.T("Close window", "Venster sluiten"), null, delegate { Raise(CloseWindowRequested); }));
            _menu.Opening += delegate { _toggleItem.Text = Expanded ? Strings.T("Fold away", "Inklappen") : Strings.T("Open", "Openen"); };

            _animTimer.Tick += OnAnimationTick;
            UpdateTitle(Native.GetText(target));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            // Clicking a bubble must never steal focus from the window you are working in.
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Render();
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------ public state

        public string Title { get { return _title; } }

        public Point CircleCenter
        {
            get { return new Point(Left + WindowSize / 2, Top + WindowSize / 2); }
        }

        public Rectangle CircleBounds
        {
            get { return new Rectangle(Left + Inset, Top + Inset, Diameter, Diameter); }
        }

        public bool IsDragging { get { return _dragging; } }

        /// <summary>WhatsApp, Discord, Gmail and friends put the unread count in front of the title.</summary>
        private static readonly Regex CountInTitle = new Regex(@"^\s*[\(\[](\d{1,4})\+?[\)\]]");

        public void UpdateTitle(string title)
        {
            _title = title ?? "";
            _tip.SetToolTip(this, _title.Length == 0 ? "Bubble" : _title);

            Match match = CountInTitle.Match(_title);
            int badge = match.Success ? int.Parse(match.Groups[1].Value) : 0;
            if (badge != _badge)
            {
                bool grew = badge > _badge;
                _badge = badge;
                if (grew && !Expanded) Pulse();
                if (IsHandleCreated) Render();
                Raise(NewsChanged);
            }
        }

        public bool HasNews { get { return _badge > 0 || _attention; } }

        public void SetAttention()
        {
            if (Expanded) return;
            _attention = true;
            Pulse();
            Raise(NewsChanged);
        }

        public void SetExpanded(bool expanded)
        {
            Expanded = expanded;
            if (expanded && _attention) { _attention = false; Raise(NewsChanged); }
            if (IsHandleCreated) Render();
        }

        /// <summary>A small dot on the top of the stack: a bubble underneath has news.</summary>
        public bool ShowDot
        {
            get { return _showDot; }
            set { if (_showDot == value) return; _showDot = value; if (IsHandleCreated) Render(); }
        }

        /// <summary>
        /// Always on top (the default): bubbles float above every window, other topmost ones
        /// included. Off: they are ordinary windows that others can cover, a full-screen
        /// video for instance.
        /// </summary>
        public static bool AlwaysOnTop = true;

        /// <summary>Put this bubble above the other windows, the expanded one included.</summary>
        public void RaiseToTop()
        {
            const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            if (AlwaysOnTop)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            }
            else
            {
                Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, flags);   // HWND_TOP
            }
        }

        // --------------------------------------------------------------- animation

        public void PopIn()
        {
            _scale = 0f;
            _popping = true;
            _popClock.Restart();
            _animTimer.Start();
        }

        public void Pulse()
        {
            _pulsesLeft = 3;
            _pulse = 0f;
            _pulseClock.Restart();
            _animTimer.Start();
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            bool busy = false;
            bool redraw = false;
            if (_popping)
            {
                double t = Math.Min(1.0, _popClock.Elapsed.TotalMilliseconds / 260.0);
                // Ease-out-back: overshoots a little, like a bubble that just formed.
                const double c = 1.9;
                double x = t - 1;
                _scale = (float)(1 + (c + 1) * x * x * x + c * x * x);
                redraw = true;
                if (t >= 1) { _popping = false; _scale = 1f; } else busy = true;
            }

            if (_pulsesLeft > 0)
            {
                const double period = 850.0;
                double elapsed = _pulseClock.Elapsed.TotalMilliseconds - (3 - _pulsesLeft) * period;
                _pulse = (float)Math.Max(0, Math.Min(1, elapsed / period));
                if (elapsed >= period) { _pulsesLeft--; }
                if (_pulsesLeft == 0) _pulse = -1f;
                redraw = true;
                busy = busy || _pulsesLeft > 0;
            }

            if (redraw) Render();
            if (!busy) _animTimer.Stop();
        }

        // ------------------------------------------------------------------- mouse

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _down = true;
            _dragging = false;
            _downCursor = Cursor.Position;
            _downLocation = Location;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_down) return;

            Point now = Cursor.Position;
            int dx = now.X - _downCursor.X, dy = now.Y - _downCursor.Y;
            int slop = Math.Max(4, Diameter / 12);

            if (!_dragging && (Math.Abs(dx) > slop || Math.Abs(dy) > slop))
            {
                _dragging = true;
                Raise(DragStarted);
            }

            if (_dragging && DragMoved != null)
                DragMoved(this, new PointEventArgs(new Point(_downLocation.X + dx, _downLocation.Y + dy)));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Right)
            {
                _menu.Show(Cursor.Position);
                return;
            }
            if (e.Button != MouseButtons.Left || !_down) return;

            _down = false;
            Capture = false;
            if (_dragging)
            {
                _dragging = false;
                Raise(DragEnded);
            }
            else Raise(Clicked);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // Capture lost mid-drag (a UAC prompt, Ctrl+Alt+Del): finish the drag where it is.
            if (_down && !Capture)
            {
                _down = false;
                if (_dragging) { _dragging = false; Raise(DragEnded); }
            }
        }

        // ----------------------------------------------------------------- drawing

        public void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;

            int size = WindowSize;
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    Draw(g, size);
                }
                LayeredWindow.Apply(this, bitmap, 255);
            }
        }

        private void Draw(Graphics g, int size)
        {
            float c = size / 2f;
            float r = Diameter / 2f * _scale;
            if (r < 1) return;

            Theme theme = Theme.Current;

            // Soft shadow, a little below the bubble.
            float shadowR = r + Inset * 0.55f * _scale;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(c - shadowR, c - shadowR + 2 * _scale, shadowR * 2, shadowR * 2);
                using (var brush = new PathGradientBrush(path))
                {
                    brush.CenterColor = Color.FromArgb(theme.Dark ? 150 : 90, 0, 0, 0);
                    brush.SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) };
                    float focus = r / shadowR;
                    brush.FocusScales = new PointF(focus * 0.9f, focus * 0.9f);
                    g.FillPath(brush, path);
                }
            }

            // Attention ring that grows outwards and fades.
            if (_pulse >= 0)
            {
                float pr = r + (Inset - 2) * _pulse;
                int alpha = (int)(200 * (1 - _pulse));
                using (var pen = new Pen(Color.FromArgb(alpha, theme.Accent), Math.Max(2f, Diameter / 20f)))
                    g.DrawEllipse(pen, c - pr, c - pr, pr * 2, pr * 2);
            }

            var circle = new RectangleF(c - r, c - r, r * 2, r * 2);
            using (var fill = new SolidBrush(theme.Bubble))
                g.FillEllipse(fill, circle);
            using (var edge = new Pen(theme.BubbleEdge, 1f))
                g.DrawEllipse(edge, circle);

            if (_icon != null)
            {
                float iconSize = Diameter * 0.56f * _scale;
                g.DrawImage(_icon, c - iconSize / 2, c - iconSize / 2, iconSize, iconSize);
            }
            else DrawInitial(g, circle);

            if (Expanded)
            {
                float er = r + Math.Max(2.5f, Diameter / 16f);
                using (var pen = new Pen(theme.Accent, Math.Max(2f, Diameter / 22f)))
                    g.DrawEllipse(pen, c - er, c - er, er * 2, er * 2);
            }

            if (_scale > 0.8f && (_badge > 0 || _attention || _showDot)) DrawBadge(g, c, r, theme);
        }

        private void DrawInitial(Graphics g, RectangleF circle)
        {
            string text = _title.Trim();
            string letter = text.Length > 0 ? char.ToUpperInvariant(text[0]).ToString() : "?";
            int hash = 0;
            foreach (char ch in text) hash = hash * 31 + ch;
            Color[] palette =
            {
                Color.FromArgb(66, 133, 244), Color.FromArgb(52, 168, 83), Color.FromArgb(234, 67, 53),
                Color.FromArgb(251, 140, 0), Color.FromArgb(142, 36, 170), Color.FromArgb(0, 137, 123)
            };
            Color color = palette[(hash & 0x7fffffff) % palette.Length];

            RectangleF inner = RectangleF.Inflate(circle, -circle.Width * 0.08f, -circle.Height * 0.08f);
            using (var fill = new SolidBrush(color)) g.FillEllipse(fill, inner);
            using (var font = new Font("Segoe UI Semibold", Math.Max(6f, inner.Height * 0.42f), GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(letter, font, white, inner, format);
        }

        private void DrawBadge(Graphics g, float c, float r, Theme theme)
        {
            float bx = c + r * 0.72f, by = c - r * 0.72f;
            string text = _badge > 99 ? "99+" : _badge > 0 ? _badge.ToString() : null;
            float h = text == null ? Diameter * 0.26f : Diameter * 0.36f;
            float w = h;

            Font font = null;
            try
            {
                if (text != null)
                {
                    font = new Font("Segoe UI", h * 0.58f, FontStyle.Bold, GraphicsUnit.Pixel);
                    w = Math.Max(h, g.MeasureString(text, font).Width * 0.92f);
                }

                var rect = new RectangleF(bx - w / 2, by - h / 2, w, h);
                using (var path = Pill(rect))
                {
                    using (var fill = new SolidBrush(Color.FromArgb(229, 57, 53))) g.FillPath(fill, path);
                    using (var ring = new Pen(theme.Bubble, Math.Max(1.5f, Diameter / 28f))) g.DrawPath(ring, path);
                }
                if (text != null)
                {
                    using (var white = new SolidBrush(Color.White))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, font, white, new RectangleF(rect.X, rect.Y + h * 0.03f, rect.Width, rect.Height), format);
                }
            }
            finally { if (font != null) font.Dispose(); }
        }

        private static GraphicsPath Pill(RectangleF r)
        {
            var path = new GraphicsPath();
            float d = r.Height;
            if (r.Width <= d) { path.AddEllipse(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer.Dispose();
                _tip.Dispose();
                _menu.Dispose();
                if (_icon != null) _icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal class PointEventArgs : EventArgs
    {
        public readonly Point Point;
        public PointEventArgs(Point point) { Point = point; }
    }
}
