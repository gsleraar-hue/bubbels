using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Bubbels;
using T = Bubbels.Strings;

namespace BubbelsSetup
{
    internal static class SetupProgram
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            bool uninstall = Has(args, "uninstall");
            bool silent = Has(args, "silent") || Has(args, "s");

            if (silent)
            {
                try
                {
                    if (uninstall) Installer.Uninstall();
                    else Installer.Install(!Has(args, "no-autostart"), Has(args, "desktop"), !Has(args, "no-launch"));
                    return 0;
                }
                catch { return 1; }
            }

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(uninstall));
            return 0;
        }

        private static bool Has(string[] args, string name)
        {
            foreach (string a in args)
                if (a.TrimStart('/', '-').Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// The setup window, drawn in the Windows 11 style: header with the icon, a card with two
    /// switches, and a footer with the buttons. Every step (working, done, confirm removal)
    /// happens in this one window instead of in message boxes.
    /// </summary>
    internal class SetupForm : Form
    {
        private enum Mode { Ready, Working, Done, ConfirmRemove, Removed, Failed }

        private readonly float _s;
        private readonly Bitmap _icon;
        private readonly ToggleSwitch _autostart = new ToggleSwitch();
        private readonly ToggleSwitch _desktop = new ToggleSwitch();
        private readonly ModernButton _primary = new ModernButton { Primary = true };
        private readonly ModernButton _secondary = new ModernButton();
        private readonly ModernButton _remove = new ModernButton { DangerStyle = true };
        private readonly ProgressLine _progress = new ProgressLine();
        private readonly Font _title, _body, _small, _strong;
        private Mode _mode = Mode.Ready;
        private string _message = "";
        private bool _removing;

        public SetupForm(bool uninstallMode)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _s = g.DpiX / 96f;

            Text = T.T("Bubbels setup", "Bubbels installeren");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(480), S(344));
            BackColor = Modern.Back;
            DoubleBuffered = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _icon = LoadIcon(S(52));

            _title = Modern.Semibold(18f);
            _body = Modern.Font(10f, FontStyle.Regular);
            _small = Modern.Font(9f, FontStyle.Regular);
            _strong = Modern.Semibold(10.5f);

            foreach (Control c in new Control[] { _primary, _secondary, _remove }) c.Font = Modern.Font(10f, FontStyle.Regular);

            _autostart.On = true;
            _desktop.On = false;
            if (Installer.IsInstalled())
            {
                _autostart.On = Installer.IsAutostartOn();
                _desktop.On = File.Exists(Installer.DesktopLink);
            }

            _primary.Click += delegate { OnPrimary(); };
            _secondary.Click += delegate { OnSecondary(); };
            _remove.Click += delegate { SetMode(Mode.ConfirmRemove); };
            _remove.Text = T.T("Uninstall", "Verwijderen");

            Controls.AddRange(new Control[] { _autostart, _desktop, _primary, _secondary, _remove, _progress });
            LayoutControls();
            SetMode(uninstallMode ? Mode.ConfirmRemove : Mode.Ready);
        }

        private int S(float v) { return (int)Math.Round(v * _s); }

        private Bitmap LoadIcon(int size)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("bubbels.ico"))
                    if (stream != null) using (var icon = new Icon(stream, size, size)) return new Bitmap(icon.ToBitmap(), size, size);
            }
            catch { }
            return null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Modern.StyleTitleBar(Handle, Modern.Back);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) OnSecondary();
            if (e.KeyCode == Keys.Enter && !(ActiveControl is ModernButton)) OnPrimary();
        }

        // ------------------------------------------------------------------ layout

        private Rectangle CardRect { get { return new Rectangle(S(28), S(144), ClientSize.Width - S(56), S(112)); } }
        private int FooterTop { get { return ClientSize.Height - S(72); } }

        private void LayoutControls()
        {
            Rectangle card = CardRect;
            Size toggle = new Size(S(40), S(20));
            _autostart.Bounds = new Rectangle(card.Right - S(16) - toggle.Width, card.Top + S(28) - toggle.Height / 2, toggle.Width, toggle.Height);
            _desktop.Bounds = new Rectangle(card.Right - S(16) - toggle.Width, card.Top + S(84) - toggle.Height / 2, toggle.Width, toggle.Height);
            _autostart.BackColor = _desktop.BackColor = Modern.Card;

            int by = FooterTop + S(20), bh = S(32);
            _primary.Bounds = new Rectangle(ClientSize.Width - S(28) - S(128), by, S(128), bh);
            _secondary.Bounds = new Rectangle(_primary.Left - S(8) - S(104), by, S(104), bh);
            _remove.Bounds = new Rectangle(S(28), by, S(112), bh);
            _primary.BackColor = _secondary.BackColor = _remove.BackColor = Modern.Footer;

            _progress.Bounds = new Rectangle(S(28), S(276), ClientSize.Width - S(56), S(6));
            _progress.BackColor = Modern.Back;
        }

        private void SetMode(Mode mode)
        {
            _mode = mode;
            bool installed = Installer.IsInstalled();
            bool ready = mode == Mode.Ready;

            _autostart.Visible = _desktop.Visible = ready || mode == Mode.Working;
            _autostart.Enabled = _desktop.Enabled = ready;
            _progress.Running = mode == Mode.Working;
            _remove.Visible = ready && installed;
            _secondary.Visible = mode == Mode.Ready || mode == Mode.ConfirmRemove || mode == Mode.Failed;
            _primary.Enabled = mode != Mode.Working;
            _primary.DangerStyle = mode == Mode.ConfirmRemove;

            switch (mode)
            {
                case Mode.Ready:
                    _primary.Text = installed ? T.T("Update", "Bijwerken") : T.T("Install", "Installeren");
                    _secondary.Text = T.T("Cancel", "Annuleren");
                    break;
                case Mode.Working:
                    _primary.Text = _removing ? T.T("Removing...", "Verwijderen...") : T.T("Installing...", "Installeren...");
                    break;
                case Mode.ConfirmRemove:
                    _primary.Text = T.T("Uninstall", "Verwijderen");
                    _secondary.Text = T.T("Cancel", "Annuleren");
                    break;
                case Mode.Failed:
                    _primary.Text = T.T("Try again", "Opnieuw");
                    _secondary.Text = T.T("Close", "Sluiten");
                    break;
                default:
                    _primary.Text = T.T("Close", "Sluiten");
                    break;
            }
            Invalidate();
            if (_primary.Visible && _primary.Enabled) _primary.Focus();
        }

        // ----------------------------------------------------------------- actions

        private void OnPrimary()
        {
            switch (_mode)
            {
                case Mode.Ready: Run(false); break;
                case Mode.ConfirmRemove: Run(true); break;
                case Mode.Failed: SetMode(Mode.Ready); break;
                case Mode.Working: break;
                default: Close(); break;
            }
        }

        private void OnSecondary()
        {
            if (_mode == Mode.Working) return;
            if (_mode == Mode.ConfirmRemove && Installer.IsInstalled()) { SetMode(Mode.Ready); return; }
            Close();
        }

        private void Run(bool remove)
        {
            _removing = remove;
            SetMode(Mode.Working);
            bool autostart = _autostart.On, desktop = _desktop.On;

            // Off the UI thread so the progress bar keeps moving; STA for the shortcut COM object.
            var worker = new Thread(delegate ()
            {
                Exception error = null;
                try
                {
                    if (remove) Installer.Uninstall();
                    else Installer.Install(autostart, desktop, true);
                }
                catch (Exception ex) { error = ex; }

                BeginInvoke(new MethodInvoker(delegate
                {
                    if (error != null)
                    {
                        _message = error.Message;
                        SetMode(Mode.Failed);
                    }
                    else SetMode(remove ? Mode.Removed : Mode.Done);
                }));
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
        }

        // ----------------------------------------------------------------- drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int left = S(28), width = ClientSize.Width - S(56);

            // Header: icon, name, version.
            if (_icon != null) g.DrawImage(_icon, left - S(2), S(26), S(52), S(52));
            TextRenderer.DrawText(g, "Bubbels", _title, new Point(S(88), S(24)), Modern.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, T.T("Version ", "Versie ") + Installer.Version, _small, new Point(S(90), S(58)),
                                  Modern.SubText, TextFormatFlags.NoPadding);

            var bodyRect = new Rectangle(left, S(96), width, S(44));
            const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

            switch (_mode)
            {
                case Mode.Ready:
                case Mode.Working:
                    TextRenderer.DrawText(g, T.T("Put any window in a floating bubble. Press Ctrl+Alt+O on a window, or pick one from the tray icon.",
                                                 "Stop elk venster in een zwevende bubbel. Druk Ctrl+Alt+O op een venster, of kies er een via het systeemvak."),
                                          _body, bodyRect, Modern.SubText, wrap);
                    DrawCard(g);
                    string below = _mode == Mode.Working
                        ? (_removing ? T.T("Removing Bubbels...", "Bubbels wordt verwijderd...") : T.T("Installing Bubbels...", "Bubbels wordt geinstalleerd..."))
                        : T.T("Installs to ", "Wordt geinstalleerd in ") + Installer.InstallDir;
                    int belowY = _mode == Mode.Working ? S(290) : S(270);
                    TextRenderer.DrawText(g, below, _small, new Rectangle(left, belowY, width, S(20)), Modern.SubText,
                                          TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.PathEllipsis);
                    break;

                case Mode.Done:
                    DrawResult(g, true, T.T("Bubbels is installed", "Bubbels is geinstalleerd"),
                               T.T("It is running now; its icon sits next to the clock. Press Ctrl+Alt+O on any window to put it in a bubble.",
                                   "Het draait al en staat bij de klok. Druk Ctrl+Alt+O op een venster om het in een bubbel te stoppen."));
                    break;

                case Mode.Removed:
                    DrawResult(g, true, T.T("Bubbels has been removed", "Bubbels is verwijderd"),
                               T.T("Every window that was in a bubble is back where it was.", "Alle vensters die in een bubbel zaten staan weer op hun plek."));
                    break;

                case Mode.ConfirmRemove:
                    TextRenderer.DrawText(g, T.T("Remove Bubbels from this computer?", "Bubbels van deze computer verwijderen?"),
                                          _strong, new Point(left, S(104)), Modern.Text, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, T.T("Windows that are in a bubble are given back first. Your settings are removed too.",
                                                 "Vensters in een bubbel komen eerst terug. Je instellingen worden ook verwijderd."),
                                          _body, new Rectangle(left, S(134), width, S(60)), Modern.SubText, wrap);
                    break;

                case Mode.Failed:
                    DrawResult(g, false, _removing ? T.T("Uninstalling failed", "Verwijderen mislukt") : T.T("Installation failed", "Installeren mislukt"), _message);
                    break;
            }

            // Footer band.
            using (var b = new SolidBrush(Modern.Footer)) g.FillRectangle(b, 0, FooterTop, ClientSize.Width, ClientSize.Height - FooterTop);
            using (var p = new Pen(Modern.Stroke)) g.DrawLine(p, 0, FooterTop, ClientSize.Width, FooterTop);
        }

        private void DrawCard(Graphics g)
        {
            Rectangle card = CardRect;
            using (var path = Modern.Round(new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1, card.Height - 1), S(6)))
            {
                using (var b = new SolidBrush(Modern.Card)) g.FillPath(b, path);
                using (var p = new Pen(Modern.Stroke)) g.DrawPath(p, path);
            }
            int mid = card.Top + card.Height / 2;
            using (var p = new Pen(Modern.Stroke)) g.DrawLine(p, card.Left + 1, mid, card.Right - 1, mid);

            DrawRow(g, card.Top, T.T("Start with Windows", "Starten met Windows"),
                    T.T("Bubbels is ready as soon as you sign in", "Bubbels staat klaar zodra je inlogt"));
            DrawRow(g, mid, T.T("Desktop shortcut", "Snelkoppeling op het bureaublad"),
                    T.T("Besides the one in the Start menu", "Naast die in het startmenu"));
        }

        private void DrawRow(Graphics g, int top, string label, string hint)
        {
            int x = CardRect.Left + S(16);
            TextRenderer.DrawText(g, label, _body, new Point(x, top + S(10)), Modern.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, hint, _small, new Point(x, top + S(31)), Modern.SubText, TextFormatFlags.NoPadding);
        }

        private void DrawResult(Graphics g, bool ok, string title, string text)
        {
            int left = S(28), width = ClientSize.Width - S(56);
            float d = S(28);
            var circle = new RectangleF(left, S(104), d, d);
            Color colour = ok ? (Modern.Dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 123, 15)) : Modern.Danger;
            using (var b = new SolidBrush(colour)) g.FillEllipse(b, circle);
            using (var pen = new Pen(Modern.Dark ? Color.Black : Color.White, Math.Max(2f, d / 10)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                float cx = circle.X + d / 2, cy = circle.Y + d / 2, s = d / 4.2f;
                if (ok) g.DrawLines(pen, new[] { new PointF(cx - s, cy), new PointF(cx - s * 0.25f, cy + s * 0.75f), new PointF(cx + s, cy - s * 0.7f) });
                else { g.DrawLine(pen, cx - s * 0.8f, cy - s * 0.8f, cx + s * 0.8f, cy + s * 0.8f); g.DrawLine(pen, cx - s * 0.8f, cy + s * 0.8f, cx + s * 0.8f, cy - s * 0.8f); }
            }
            TextRenderer.DrawText(g, title, _strong, new Point(left + S(40), S(108)), Modern.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, text ?? "", _body, new Rectangle(left, S(148), width, S(100)), Modern.SubText,
                                  TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_icon != null) _icon.Dispose();
                _title.Dispose(); _body.Dispose(); _small.Dispose(); _strong.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
