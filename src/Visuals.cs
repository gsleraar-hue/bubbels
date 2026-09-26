using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bubbels
{
    /// <summary>Pushes a 32-bit bitmap into a WS_EX_LAYERED window, alpha channel and all.</summary>
    internal static class LayeredWindow
    {
        public static void Apply(Form form, Bitmap bitmap, byte opacity)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                // GetHbitmap hands back premultiplied pixels, which is what ULW_ALPHA wants.
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBitmap);

                var size = new Native.SIZE(bitmap.Width, bitmap.Height);
                var source = new Native.POINT(0, 0);
                var position = new Native.POINT(form.Left, form.Top);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    SourceConstantAlpha = opacity,
                    AlphaFormat = Native.AC_SRC_ALPHA
                };
                Native.UpdateLayeredWindow(form.Handle, screenDc, ref position, ref size,
                                           memDc, ref source, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBitmap);
                }
                Native.DeleteDC(memDc);
            }
        }
    }

    /// <summary>Follows the Windows light/dark setting and the accent colour.</summary>
    internal class Theme
    {
        public bool Dark;
        public Color Accent;
        public Color Bubble;
        public Color BubbleEdge;

        private static Theme _current;
        public static Theme Current
        {
            get { if (_current == null) _current = Read(); return _current; }
        }

        public static void Refresh() { _current = Read(); }

        private static Theme Read()
        {
            var theme = new Theme { Dark = false, Accent = Color.FromArgb(0, 103, 192) };
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object light = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (light is int) theme.Dark = (int)light == 0;
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object accent = key == null ? null : key.GetValue("AccentColor");
                    if (accent is int)
                    {
                        // Stored as 0xAABBGGRR.
                        int abgr = (int)accent;
                        theme.Accent = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                    }
                }
            }
            catch { }

            theme.Bubble = theme.Dark ? Color.FromArgb(44, 44, 48) : Color.White;
            theme.BubbleEdge = theme.Dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);
            return theme;
        }
    }

    /// <summary>Finds the best-looking icon for somebody else's window.</summary>
    internal static class WindowIcons
    {
        private const int ICON_BIG = 1, ICON_SMALL2 = 2;
        private const int GCLP_HICON = -14;
        private const uint SMTO_ABORTIFHUNG = 0x2;

        public static Bitmap Get(IntPtr hwnd, int wanted)
        {
            Bitmap fromWindow = null;
            try
            {
                // The window's own icon comes first: an installed web app (WhatsApp, Teams as
                // a PWA) runs inside chrome.exe or msedge.exe but sets its own icon here.
                IntPtr handle;
                Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero,
                                          SMTO_ABORTIFHUNG, 200, out handle);
                if (handle == IntPtr.Zero) handle = Native.GetClassLongPtr(hwnd, GCLP_HICON);
                if (handle == IntPtr.Zero)
                    Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero,
                                              SMTO_ABORTIFHUNG, 200, out handle);
                if (handle != IntPtr.Zero)
                    using (Icon icon = Icon.FromHandle(handle)) fromWindow = icon.ToBitmap();
            }
            catch (Exception ex) { Log.Exception("window icon", ex); }

            string exe = Native.GetProcessPath(Native.GetPid(hwnd));
            bool frameHost = exe != null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

            // Window icons are usually 32 px, which looks soft in a bubble. If the window just
            // uses the icon of its exe, fetch a bigger rendition of that same icon instead.
            if (exe != null && !frameHost && (fromWindow == null || fromWindow.Width < wanted))
            {
                Bitmap small = ExtractFromFile(exe, fromWindow == null ? 32 : fromWindow.Width);
                bool same = fromWindow == null || (small != null && LooksAlike(small, fromWindow));
                if (small != null) small.Dispose();

                if (same)
                {
                    Bitmap large = ExtractFromFile(exe, wanted <= 48 ? 48 : wanted <= 64 ? 64 : 128);
                    if (large != null)
                    {
                        if (fromWindow != null) fromWindow.Dispose();
                        return large;
                    }
                }
            }
            return fromWindow;
        }

        private static Bitmap ExtractFromFile(string file, int size)
        {
            var icons = new IntPtr[1];
            var ids = new int[1];
            try
            {
                uint count = Native.PrivateExtractIcons(file, 0, size, size, icons, ids, 1, 0);
                if (count == 0 || count == 0xFFFFFFFF || icons[0] == IntPtr.Zero) return null;
                using (Icon icon = Icon.FromHandle(icons[0])) return icon.ToBitmap();
            }
            catch { return null; }
            finally { if (icons[0] != IntPtr.Zero) Native.DestroyIcon(icons[0]); }
        }

        /// <summary>Compare two small icons by their downscaled pixels.</summary>
        private static bool LooksAlike(Bitmap a, Bitmap b)
        {
            const int n = 8;
            using (var sa = new Bitmap(a, n, n))
            using (var sb = new Bitmap(b, n, n))
            {
                long diff = 0;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        Color ca = sa.GetPixel(x, y), cb = sb.GetPixel(x, y);
                        diff += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) +
                                Math.Abs(ca.B - cb.B) + Math.Abs(ca.A - cb.A);
                    }
                return diff / (n * n) < 60;
            }
        }
    }

    /// <summary>The round "drop here to let go" target that appears while you drag a bubble.</summary>
    internal class DismissTarget : Form
    {
        private readonly int _diameter;
        private readonly int _size;
        private bool _hot;

        // Springy appearance: it rises from below and swells when a bubble comes close.
        private readonly Timer _timer = new Timer { Interval = 15 };
        private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        private float _appear, _hotAmount, _hotVelocity;
        private double _last;
        private Point _restLocation;

        public DismissTarget(float dpiScale)
        {
            _diameter = (int)Math.Round(60 * dpiScale);
            _size = (int)Math.Round(_diameter * 1.7f);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(_size, _size);
            Text = "Release bubble";
            _timer.Tick += OnTick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST |
                              Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public Point Center { get { return new Point(_restLocation.X + _size / 2, _restLocation.Y + _size / 2); } }

        /// <summary>How close a bubble centre has to come before it gets pulled in.</summary>
        public int CatchRadius { get { return _diameter; } }

        public void ShowOn(Screen screen)
        {
            Rectangle area = screen.WorkingArea;
            _restLocation = new Point(area.Left + (area.Width - _size) / 2, area.Bottom - _size - _diameter / 4);
            _hot = false;
            _hotAmount = 0;
            _hotVelocity = 0;
            _appear = 0;
            Location = new Point(_restLocation.X, _restLocation.Y + _diameter);
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            _clock.Restart();
            _last = 0;
            _timer.Start();
            Render();
        }

        public new void Hide()
        {
            _timer.Stop();
            base.Hide();
        }

        public bool Hot
        {
            get { return _hot; }
            set { if (_hot == value) return; _hot = value; _timer.Start(); }
        }

        private void OnTick(object sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, now - _last);
            _last = now;

            _appear = (float)Math.Min(1.0, now / 0.22);
            float eased = 1 - (float)Math.Pow(1 - _appear, 3);
            Location = new Point(_restLocation.X, _restLocation.Y + (int)Math.Round(_diameter * (1 - eased)));

            // Underdamped spring for the swell, so it wobbles a little when it catches a bubble.
            float target = _hot ? 1f : 0f;
            for (float t = 0; t < dt; t += 0.004f)
            {
                float a = 500f * (target - _hotAmount) - 18f * _hotVelocity;
                _hotVelocity += a * 0.004f;
                _hotAmount += _hotVelocity * 0.004f;
            }

            Render();
            if (_appear >= 1 && Math.Abs(target - _hotAmount) < 0.002f && Math.Abs(_hotVelocity) < 0.01f)
            {
                _hotAmount = target;
                _timer.Stop();
                Render();
            }
        }

        private void Render()
        {
            if (!IsHandleCreated) return;
            float eased = 1 - (float)Math.Pow(1 - _appear, 3);
            using (var bitmap = new Bitmap(_size, _size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    float c = _size / 2f;
                    float r = _diameter / 2f * (0.7f + 0.3f * eased) * (1f + 0.25f * _hotAmount);
                    float hot = Math.Max(0, Math.Min(1, _hotAmount));
                    Color idle = Color.FromArgb(200, 32, 33, 36), red = Color.FromArgb(235, 211, 47, 47);
                    Color fillColor = Color.FromArgb(
                        (int)(idle.A + (red.A - idle.A) * hot), (int)(idle.R + (red.R - idle.R) * hot),
                        (int)(idle.G + (red.G - idle.G) * hot), (int)(idle.B + (red.B - idle.B) * hot));

                    using (var fill = new SolidBrush(fillColor))
                        g.FillEllipse(fill, c - r, c - r, r * 2, r * 2);
                    using (var edge = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
                        g.DrawEllipse(edge, c - r, c - r, r * 2, r * 2);

                    float x = r * 0.32f;
                    using (var pen = new Pen(Color.White, Math.Max(2.5f, _diameter / 18f)))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        g.DrawLine(pen, c - x, c - x, c + x, c + x);
                        g.DrawLine(pen, c - x, c + x, c + x, c - x);
                    }
                }
                LayeredWindow.Apply(this, bitmap, (byte)(255 * eased));
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
