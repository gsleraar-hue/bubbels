using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using T = Bubbels.Strings;

namespace BubbelsSetup
{
    internal static class SetupProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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

    internal class SetupForm : Form
    {
        private readonly CheckBox _autostart;
        private readonly CheckBox _desktop;
        private readonly Button _action;
        private readonly Button _remove;
        private readonly Label _status;

        public SetupForm(bool uninstallMode)
        {
            Text = T.T("Install Bubbels", "Bubbels installeren");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(460, 256);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label
            {
                Text = "Bubbels " + Installer.Version,
                Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                Location = new Point(18, 14),
                Size = new Size(420, 30)
            };

            var blurb = new Label
            {
                Text = T.T("Put any window in a floating bubble, like bubbles on Android.\r\n" +
                           "Press Ctrl+Alt+O on a window, or pick one from the tray icon.",
                           "Stop elk venster in een zwevende bubbel, zoals op Android.\r\n" +
                           "Druk Ctrl+Alt+O op een venster, of kies er een via het systeemvak."),
                Location = new Point(20, 46),
                Size = new Size(422, 36)
            };

            var where = new Label
            {
                Text = T.T("Installs to: ", "Wordt geinstalleerd in: ") + Installer.InstallDir,
                ForeColor = SystemColors.GrayText,
                Location = new Point(20, 90),
                Size = new Size(422, 18),
                AutoEllipsis = true
            };

            _autostart = new CheckBox
            {
                Text = T.T("Start with Windows", "Starten met Windows"),
                Checked = true,
                Location = new Point(20, 116),
                Size = new Size(422, 22)
            };

            _desktop = new CheckBox
            {
                Text = T.T("Shortcut on the desktop", "Snelkoppeling op het bureaublad"),
                Checked = false,
                Location = new Point(20, 140),
                Size = new Size(422, 22)
            };

            _status = new Label
            {
                Location = new Point(20, 174),
                Size = new Size(422, 20),
                ForeColor = SystemColors.GrayText
            };

            _action = new Button
            {
                Text = Installer.IsInstalled() ? T.T("Update", "Bijwerken") : T.T("Install", "Installeren"),
                Location = new Point(248, 210),
                Size = new Size(96, 30)
            };
            _action.Click += delegate { DoInstall(); };

            _remove = new Button
            {
                Text = T.T("Uninstall", "Verwijderen"),
                Location = new Point(20, 210),
                Size = new Size(104, 30),
                Enabled = Installer.IsInstalled()
            };
            _remove.Click += delegate { DoUninstall(); };

            var close = new Button
            {
                Text = T.T("Close", "Sluiten"),
                DialogResult = DialogResult.Cancel,
                Location = new Point(350, 210),
                Size = new Size(92, 30)
            };

            Controls.AddRange(new Control[] { title, blurb, where, _autostart, _desktop, _status, _action, _remove, close });
            AcceptButton = _action;
            CancelButton = close;

            if (Installer.IsInstalled())
            {
                _autostart.Checked = Installer.IsAutostartOn();
                _desktop.Checked = File.Exists(Installer.DesktopLink);
            }

            // Started from Settings > Apps: go straight to removing.
            if (uninstallMode) Shown += delegate { DoUninstall(); };
        }

        private void DoInstall()
        {
            try
            {
                Busy(T.T("Installing...", "Bezig met installeren..."));
                Installer.Install(_autostart.Checked, _desktop.Checked, true);
                _action.Text = T.T("Update", "Bijwerken");
                Done(T.T("Done. Bubbels is running; its icon sits next to the clock.",
                         "Klaar. Bubbels draait en staat bij de klok."));
            }
            catch (Exception ex)
            {
                Done("");
                MessageBox.Show(T.T("Installation failed:", "Installeren mislukt:") + "\r\n\r\n" + ex.Message, "Bubbels",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DoUninstall()
        {
            if (MessageBox.Show(T.T("Remove Bubbels from this computer? Windows in bubbles are given back first.",
                                    "Bubbels van deze computer verwijderen? Vensters in bubbels komen eerst terug."),
                                "Bubbels", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                Busy(T.T("Uninstalling...", "Bezig met verwijderen..."));
                Installer.Uninstall();
                MessageBox.Show(T.T("Bubbels has been removed.", "Bubbels is verwijderd."), "Bubbels",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                Application.Exit();
            }
            catch (Exception ex)
            {
                Done("");
                MessageBox.Show(T.T("Uninstalling failed:", "Verwijderen mislukt:") + "\r\n\r\n" + ex.Message, "Bubbels",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Busy(string text)
        {
            _status.Text = text;
            _action.Enabled = false;
            _remove.Enabled = false;
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
        }

        private void Done(string text)
        {
            _status.Text = text;
            _action.Enabled = true;
            _remove.Enabled = Installer.IsInstalled();
            Cursor = Cursors.Default;
        }
    }
}
