using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bubbels
{
    /// <summary>
    /// Windows 11 look for plain WinForms: colours that follow light/dark mode and the accent
    /// colour, a title bar in the same colour, rounded menus, switches and buttons.
    /// Shared by the app and the setup.
    /// </summary>
    internal static class Modern
    {
        public static bool Dark;
        public static Color Accent = Color.FromArgb(0, 103, 192);

        static Modern() { Refresh(); }

        public static void Refresh()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object light = key == null ? null : key.GetValue("AppsUseLightTheme");
                    Dark = light is int && (int)light == 0;
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object accent = key == null ? null : key.GetValue("AccentColor");
                    if (accent is int)
                    {
                        int abgr = (int)accent;   // stored as 0xAABBGGRR
                        Accent = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                    }
                }
            }
            catch { }
        }

        // Palette after the Windows 11 design kit.
        public static Color Back { get { return Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243); } }
        public static Color Footer { get { return Dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(238, 238, 238); } }
        public static Color Card { get { return Dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(251, 251, 251); } }
        public static Color Stroke { get { return Dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(229, 229, 229); } }
        public static Color Text { get { return Dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(27, 27, 27); } }
        public static Color SubText { get { return Dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(96, 96, 96); } }
        public static Color Hover { get { return Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0); } }
        public static Color Danger { get { return Dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(196, 43, 28); } }

        /// <summary>Accent as Windows uses it on filled buttons: darker on light, lighter on dark.</summary>
        public static Color AccentFill { get { return Dark ? Mix(Accent, Color.White, 0.45f) : Mix(Accent, Color.Black, 0.18f); } }
        public static Color OnAccent { get { return Dark ? Color.Black : Color.White; } }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                                  (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        private static string _family;
        public static Font Font(float size, FontStyle style)
        {
            if (_family == null)
            {
                _family = "Segoe UI";
                foreach (FontFamily f in FontFamily.Families)
                    if (f.Name == "Segoe UI Variable Text") { _family = f.Name; break; }
            }
            return new Font(_family, size, style, GraphicsUnit.Point);
        }

        public static Font Semibold(float size)
        {
            // GDI+ cannot pick a weight of a variable font, so use the classic semibold face.
            var font = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point);
            return font.Name == "Segoe UI Semibold" ? font : Font(size, FontStyle.Bold);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------ window chrome

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33,
                          DWMWA_CAPTION_COLOR = 35, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;

        /// <summary>Dark title bar in dark mode, and a caption in the window's own colour.</summary>
        public static void StyleTitleBar(IntPtr hwnd, Color caption)
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            int colour = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colour, 4);
        }

        public static void RoundCorners(IntPtr hwnd, bool small)
        {
            int pref = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        }

        /// <summary>Give a context menu (and its submenus) the Windows 11 look.</summary>
        public static void Apply(ContextMenuStrip menu)
        {
            menu.Renderer = new ModernMenuRenderer();
            menu.Font = Font(9.5f, FontStyle.Regular);
            menu.Padding = new Padding(4, 4, 4, 4);
            menu.ShowImageMargin = true;
        }
    }

    /// <summary>Flat, rounded, theme-aware menus instead of the Office 2003 gradient ones.</summary>
    internal class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        public ModernMenuRenderer() : base(new ProfessionalColorTable()) { RoundedEdges = false; }

        protected override void Initialize(ToolStrip toolStrip)
        {
            base.Initialize(toolStrip);
            toolStrip.BackColor = Modern.Card;
            toolStrip.ForeColor = Modern.Text;
            var dropDown = toolStrip as ToolStripDropDown;
            if (dropDown != null)
            {
                dropDown.Padding = new Padding(4, 4, 4, 4);
                if (dropDown.IsHandleCreated) Modern.RoundCorners(dropDown.Handle, false);
                dropDown.HandleCreated += delegate { Modern.RoundCorners(dropDown.Handle, false); };
            }
        }

        protected override void InitializeItem(ToolStripItem item)
        {
            base.InitializeItem(item);
            if (!(item is ToolStripSeparator)) item.Padding = new Padding(4, 5, 4, 5);
            var menuItem = item as ToolStripMenuItem;
            if (menuItem != null && menuItem.DropDown != null) menuItem.DropDown.Renderer = this;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Modern.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var p = new Pen(Modern.Stroke)) e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using (var path = Modern.Round(r, 4))
            using (var b = new SolidBrush(Modern.Hover))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Modern.Text : Modern.SubText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Modern.Stroke)) e.Graphics.DrawLine(p, 4, y, e.Item.Width - 4, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A plain check mark, no box.
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.ImageRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Min(r.Width, r.Height) / 2f;
            using (var pen = new Pen(Modern.Text, Math.Max(1.5f, s / 4f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[] { new PointF(cx - s * 0.6f, cy), new PointF(cx - s * 0.15f, cy + s * 0.45f), new PointF(cx + s * 0.65f, cy - s * 0.5f) });
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.ArrowRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 3.5f;
            using (var pen = new Pen(e.Item.Enabled ? Modern.Text : Modern.SubText, 1.4f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) });
            }
        }
    }

    /// <summary>The Windows 11 on/off switch.</summary>
    internal class ToggleSwitch : Control
    {
        private bool _on;
        public event EventHandler Toggled;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool On
        {
            get { return _on; }
            set { if (_on == value) return; _on = value; Invalidate(); }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled) return;
            On = !On;
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Modern.Card)) g.FillRectangle(bg, ClientRectangle);

            float h = Height - 2, w = Math.Min(Width - 2, h * 2);
            var track = new RectangleF(Width - w - 1, 1, w, h);
            float alpha = Enabled ? 1f : 0.45f;
            using (var path = Modern.Round(track, h / 2))
            {
                if (_on)
                {
                    using (var fill = new SolidBrush(Color.FromArgb((int)(255 * alpha), Modern.AccentFill))) g.FillPath(fill, path);
                }
                else
                {
                    using (var pen = new Pen(Color.FromArgb((int)(255 * alpha), Modern.SubText), 1.2f)) g.DrawPath(pen, path);
                }
            }
            float knob = h * 0.5f;
            float kx = _on ? track.Right - h / 2 - knob / 2 : track.X + h / 2 - knob / 2;
            Color k = _on ? Modern.OnAccent : Modern.SubText;
            using (var fill = new SolidBrush(Color.FromArgb((int)(255 * alpha), k)))
                g.FillEllipse(fill, kx, track.Y + (h - knob) / 2, knob, knob);

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Modern.Text, 1f) { DashStyle = DashStyle.Dot })
                    g.DrawPath(pen, Modern.Round(RectangleF.Inflate(track, 0.5f, 0.5f), h / 2));
        }
    }

    /// <summary>Rounded push button; Primary uses the accent colour.</summary>
    internal class ModernButton : Control
    {
        public bool Primary;
        public bool DangerStyle;
        private bool _hover, _down;

        public ModernButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Modern.Back)) g.FillRectangle(bg, ClientRectangle);

            Color fill, text, border;
            if (Primary)
            {
                Color baseColor = DangerStyle ? Modern.Danger : Modern.AccentFill;
                fill = !Enabled ? Modern.Mix(baseColor, Modern.Back, 0.6f)
                     : _down ? Modern.Mix(baseColor, Modern.Back, 0.2f)
                     : _hover ? Modern.Mix(baseColor, Modern.Back, 0.1f) : baseColor;
                text = DangerStyle && Modern.Dark ? Color.Black : Modern.OnAccent;
                border = fill;
            }
            else
            {
                int grey = Modern.Dark ? (_down ? 40 : _hover ? 60 : 50) : (_down ? 245 : _hover ? 249 : 254);
                fill = Color.FromArgb(grey, grey, grey);
                text = Enabled ? (DangerStyle ? Modern.Danger : Modern.Text) : Modern.SubText;
                border = Modern.Stroke;
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Modern.Round(r, 4))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
                if (Focused && ShowFocusCues)
                    using (var p = new Pen(Modern.Text, 1.5f)) g.DrawPath(p, Modern.Round(RectangleF.Inflate(r, 1.5f, 1.5f), 5));
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>A thin indeterminate progress bar, as Windows 11 shows while it works.</summary>
    internal class ProgressLine : Control
    {
        private readonly Timer _timer = new Timer { Interval = 16 };
        private float _phase;

        public ProgressLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            _timer.Tick += delegate { _phase = (_phase + 0.012f) % 1.4f; Invalidate(); };
        }

        public bool Running
        {
            get { return _timer.Enabled; }
            set { _timer.Enabled = value; Visible = value; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Modern.Back)) g.FillRectangle(bg, ClientRectangle);
            float y = Height / 2f, h = Math.Max(2f, Height / 2f);
            using (var track = new Pen(Modern.Stroke, 1f)) g.DrawLine(track, 0, y, Width, y);
            float start = (_phase - 0.4f) * Width, len = Width * 0.4f;
            float x0 = Math.Max(0, start), x1 = Math.Min(Width, start + len);
            if (x1 > x0)
                using (var path = Modern.Round(new RectangleF(x0, y - h / 2, x1 - x0, h), h / 2))
                using (var b = new SolidBrush(Modern.AccentFill)) g.FillPath(b, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
