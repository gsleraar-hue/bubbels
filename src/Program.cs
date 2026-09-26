using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bubbels
{
    internal static class Program
    {
        public static string DataFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bubbels"); }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Log.Write("--- start (pid {0})", Process.GetCurrentProcess().Id);

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log.Exception("unhandled error", e.ExceptionObject as Exception);
                TrayContext.EmergencyRelease();
            };
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Log.Exception("error in message loop", e.Exception);
            };

            // "--check-update" logs what the updater sees, without installing anything.
            if (args.Length == 1 && args[0] == "--check-update")
            {
                try
                {
                    Updater.Release r = Updater.FindNewer();
                    Log.Write("check-update: current {0}, newer: {1}", Updater.Current,
                              r == null ? "none" : r.Tag + " " + r.SetupUrl);
                }
                catch (Exception ex) { Log.Exception("check-update", ex); }
                return;
            }
            bool isFirst;
            using (var single = new Mutex(true, "Bubbels.SingleInstance", out isFirst))
            {
                if (!isFirst) { Log.Write("stopt: Bubbels draait al"); return; }

                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // "--selftest <hwnd> [hwnd]" runs bubble / open / fold / release on one window and
                // writes what happened to the log. For checking a build without clicking.
                if (args.Length >= 2 && (args[0] == "--selftest" || args[0] == "--zelftest"))
                {
                    var windows = new IntPtr[args.Length - 1];
                    for (int i = 1; i < args.Length; i++) windows[i - 1] = new IntPtr(long.Parse(args[i]));
                    SelfTest.Run(windows);
                    return;
                }
                Application.Run(new TrayContext());
                Log.Write("--- exited");
            }
        }
    }

    internal class TrayContext : ApplicationContext
    {
        private const int HotkeyId = 0xB0B;
        private const Keys HotkeyKey = Keys.O;
        private const uint HotkeyMods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        private const string HotkeyText = "Ctrl+Alt+O";

        private static BubbleManager _instance;

        private readonly MessageWindow _window;
        private readonly NotifyIcon _tray;
        private readonly BubbleManager _bubbles;
        private readonly ToolStripMenuItem _pickItem;
        private readonly ToolStripMenuItem _releaseAllItem;
        private readonly ToolStripMenuItem _autostartItem;
        private readonly ToolStripMenuItem _onTopItem;
        private readonly ToolStripMenuItem _autoUpdateItem;
        private readonly ToolStripMenuItem _updateItem;
        private readonly ToolStripMenuItem _checkItem;
        private System.Windows.Forms.Timer _updateTimer;
        private Updater.Release _pendingUpdate;
        private int _checking;
        private bool _hotkeyOk;

        public TrayContext()
        {
            _window = new MessageWindow();
            _bubbles = new BubbleManager();
            _instance = _bubbles;

            _pickItem = new ToolStripMenuItem(Strings.T("Put a window in a bubble", "Venster in een bubbel"));
            _pickItem.DropDownItems.Add("...");
            _pickItem.DropDownOpening += delegate { FillPicker(); };

            _releaseAllItem = new ToolStripMenuItem(Strings.T("Release all bubbles", "Alle bubbels terugzetten"), null, delegate { _bubbles.ReleaseAll(); });
            Bubble.AlwaysOnTop = Settings.AlwaysOnTop;
            _autoUpdateItem = new ToolStripMenuItem(Strings.T("Update automatically", "Automatisch bijwerken"), null, delegate
            {
                Settings.AutoUpdate = !Settings.AutoUpdate;
                _autoUpdateItem.Checked = Settings.AutoUpdate;
            }) { Checked = Settings.AutoUpdate };
            _checkItem = new ToolStripMenuItem(Strings.T("Check for updates", "Zoeken naar updates"), null, delegate { CheckForUpdate(true); });
            _updateItem = new ToolStripMenuItem("", null, delegate { InstallPendingUpdate(); }) { Visible = false, Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            _onTopItem = new ToolStripMenuItem(Strings.T("Bubbles always on top", "Bubbels altijd bovenop"), null, delegate
            {
                Bubble.AlwaysOnTop = !Bubble.AlwaysOnTop;
                Settings.AlwaysOnTop = Bubble.AlwaysOnTop;
                _onTopItem.Checked = Bubble.AlwaysOnTop;
                _bubbles.ApplyTopmost();
                Log.Write("always on top: {0}", Bubble.AlwaysOnTop);
            }) { Checked = Bubble.AlwaysOnTop };
            _autostartItem = new ToolStripMenuItem(Strings.T("Start with Windows", "Starten met Windows"), null, ToggleAutostart);

            var menu = new ContextMenuStrip();
            Modern.Apply(menu);
            menu.Items.Add(_pickItem);
            menu.Items.Add(new ToolStripMenuItem(Strings.T("Bubble the active window: ", "Actief venster bubbelen: ") + HotkeyText) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_releaseAllItem);
            menu.Items.Add(_onTopItem);
            menu.Items.Add(_autostartItem);
            menu.Items.Add(_autoUpdateItem);
            menu.Items.Add(_checkItem);
            menu.Items.Add(_updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(Strings.T("Quit", "Afsluiten"), null, delegate { Quit(); }));
            menu.Opening += delegate
            {
                _releaseAllItem.Enabled = _bubbles.Count > 0;
                _autostartItem.Checked = Autostart.IsOn();
            };

            WaitForTaskbar();
            _tray = new NotifyIcon { Icon = LoadIcon(), Visible = true, ContextMenuStrip = menu };
            _tray.MouseUp += delegate (object s, MouseEventArgs e)
            {
                // A left click opens the same menu; that is where the window list lives.
                if (e.Button != MouseButtons.Left) return;
                MethodInfo show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (show != null) show.Invoke(_tray, null);
            };

            _bubbles.Notice += delegate (string text) { Balloon(text); };
            _bubbles.Changed += delegate { UpdateTooltip(); };

            _window.HotkeyPressed += delegate { _bubbles.ToggleForeground(); };
            _window.ShellMessage += delegate (int code, IntPtr hwnd) { _bubbles.OnShellMessage(code, hwnd); };
            _window.TaskbarRecreated += delegate
            {
                Log.Write("explorer restarted: re-adding the tray icon");
                try { _tray.Visible = false; _tray.Visible = true; } catch { }
                _bubbles.ResetTaskbar();
            };

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            SystemEvents.SessionEnding += OnSessionEnding;

            ListenForQuitSignal();
            StartUpdateChecks();
            _hotkeyOk = Native.RegisterHotKey(_window.Handle, HotkeyId, HotkeyMods, (uint)HotkeyKey);
            if (!_hotkeyOk) Log.Write("hotkey {0} is already taken", HotkeyText);
            UpdateTooltip();

            string version = Updater.Current.ToString(3);
            string previous = Settings.LastVersion;
            if (previous != version) Settings.LastVersion = version;
            bool justUpdated = previous.Length > 0 && previous != version;

            string seen = Path.Combine(Program.DataFolder, "welcome-seen");
            if (justUpdated)
                Balloon(Strings.T("Bubbels has been updated to version ", "Bubbels is bijgewerkt naar versie ") + version + ".");
            else if (!_hotkeyOk)
                Balloon(HotkeyText + Strings.T(" is already used by another program. Pick windows from this icon instead.",
                                             " is al in gebruik door een ander programma. Kies vensters via dit icoon."));
            else if (!File.Exists(seen))
            {
                Balloon(Strings.T("Click a window and press " + HotkeyText + " to put it in a bubble. " +
                                  "Or click this icon and pick a window.",
                                  "Klik op een venster en druk " + HotkeyText + " om het in een bubbel te stoppen. " +
                                  "Of klik op dit icoon en kies een venster."));
                try { Directory.CreateDirectory(Program.DataFolder); File.WriteAllText(seen, DateTime.Now.ToString("s")); } catch { }
            }
            Log.Write("ready, hotkey {0}: {1}", HotkeyText, _hotkeyOk ? "ok" : "taken");
        }

        private static void WaitForTaskbar()
        {
            var clock = Stopwatch.StartNew();
            while (Native.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero && clock.Elapsed.TotalSeconds < 90)
                Thread.Sleep(250);
        }

        private void FillPicker()
        {
            _pickItem.DropDownItems.Clear();
            foreach (IntPtr hwnd in _bubbles.ListCandidates())
            {
                IntPtr target = hwnd;
                string title = Native.GetText(hwnd).Trim();
                if (title.Length > 60) title = title.Substring(0, 57) + "...";
                var item = new ToolStripMenuItem(title.Replace("&", "&&"), null, delegate { _bubbles.Add(target); });
                try { item.Image = WindowIcons.Get(hwnd, 16); } catch { }
                _pickItem.DropDownItems.Add(item);
            }
            if (_pickItem.DropDownItems.Count == 0)
                _pickItem.DropDownItems.Add(new ToolStripMenuItem(Strings.T("No windows found", "Geen vensters gevonden")) { Enabled = false });
        }

        private void UpdateTooltip()
        {
            string text = "Bubbels";
            if (_bubbles.Count > 0) text += " - " + _bubbles.Count + (_bubbles.Count == 1 ? Strings.T(" bubble", " bubbel") : Strings.T(" bubbles", " bubbels"));
            if (_hotkeyOk) text += " - " + HotkeyText;
            _tray.Text = text.Length > 62 ? text.Substring(0, 62) : text;
        }

        private void Balloon(string text)
        {
            _tray.BalloonTipTitle = "Bubbels";
            _tray.BalloonTipText = text;
            _tray.ShowBalloonTip(6000);
        }

        private void ToggleAutostart(object sender, EventArgs e)
        {
            try
            {
                bool wanted = !Autostart.IsOn();
                Autostart.Set(wanted, Application.ExecutablePath);
                Log.Write("autostart {0}", wanted ? "on" : "off");
            }
            catch (Exception ex)
            {
                Log.Exception("changing autostart", ex);
                MessageBox.Show(Strings.T("Could not change the autostart: ", "Kon de autostart niet wijzigen: ") + ex.Message, "Bubbels",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnDisplayChanged(object sender, EventArgs e) { _bubbles.KeepOnScreen(); }

        private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color ||
                e.Category == UserPreferenceCategory.VisualStyle)
            {
                Modern.Refresh();
                _bubbles.Rerender();
            }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            // Give every window back before logoff, so apps can ask their own questions.
            Log.Write("logging off: releasing everything");
            _bubbles.ReleaseAll();
        }

        /// <summary>Last resort from the crash handler: hidden windows must not stay hidden.</summary>
        public static void EmergencyRelease()
        {
            try { if (_instance != null) _instance.ReleaseAll(); } catch { }
        }

        private Icon LoadIcon()
        {
            try
            {
                Icon own = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (own != null) return new Icon(own, SystemInformation.SmallIconSize);
            }
            catch { }
            using (var bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(66, 133, 244))) g.FillEllipse(b, 2, 2, 28, 28);
                }
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }

        /// <summary>
        /// The setup (or anything else) can ask a running Bubbels to quit politely, so that
        /// every bubbled window is given back first instead of dying hidden with the process.
        /// </summary>
        public const string QuitSignalName = @"Local\Bubbels.Quit";
        private EventWaitHandle _quitSignal;
        private Control _invoker;
        private bool _quitting;

        // ----------------------------------------------------------------- updates

        private void StartUpdateChecks()
        {
            // First look shortly after start (not during logon rush), then every six hours.
            _updateTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
            _updateTimer.Tick += delegate
            {
                _updateTimer.Interval = 6 * 60 * 60 * 1000;
                CheckForUpdate(false);
            };
            _updateTimer.Start();
        }

        private void CheckForUpdate(bool manual)
        {
            if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
            var worker = new Thread(delegate ()
            {
                Updater.Release found = null;
                Exception error = null;
                try { found = Updater.FindNewer(); }
                catch (Exception ex) { error = ex; }
                finally { Interlocked.Exchange(ref _checking, 0); }

                try { _invoker.BeginInvoke(new MethodInvoker(delegate { OnUpdateChecked(found, error, manual); })); }
                catch { }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void OnUpdateChecked(Updater.Release found, Exception error, bool manual)
        {
            if (_quitting) return;
            if (error != null)
            {
                Log.Write("update check failed: {0}", error.Message);
                if (manual) Balloon(Strings.T("Could not check for updates: ", "Kon niet zoeken naar updates: ") + error.Message);
                return;
            }
            if (found == null)
            {
                if (manual) Balloon(Strings.T("Bubbels is up to date.", "Bubbels is bijgewerkt.") + " (" + Updater.Current.ToString(3) + ")");
                return;
            }

            Log.Write("update available: {0}", found.Tag);
            _pendingUpdate = found;
            _updateItem.Text = Updater.IsInstalled
                ? Strings.T("Update to ", "Bijwerken naar ") + found.Tag
                : Strings.T("Download version ", "Versie downloaden: ") + found.Tag;
            _updateItem.Visible = true;

            // Silent update only when it cannot disturb: installed copy, nothing in a bubble.
            if (Updater.IsInstalled && Settings.AutoUpdate && _bubbles.Count == 0 && found.SetupUrl != null)
            {
                InstallPendingUpdate();
                return;
            }
            Balloon(Strings.T("Version " + found.Tag + " is available. Choose it in the menu to update.",
                              "Versie " + found.Tag + " is beschikbaar. Kies bijwerken in het menu."));
        }

        private void InstallPendingUpdate()
        {
            Updater.Release release = _pendingUpdate;
            if (release == null) return;
            if (!Updater.IsInstalled || release.SetupUrl == null)
            {
                Process.Start(Updater.ReleasesPage);
                return;
            }

            _updateItem.Enabled = false;
            _updateItem.Text = Strings.T("Downloading ", "Bezig met downloaden: ") + release.Tag + "...";
            var worker = new Thread(delegate ()
            {
                try { Updater.Install(release); }   // the setup will ask us to quit
                catch (Exception ex)
                {
                    Log.Exception("update", ex);
                    try
                    {
                        _invoker.BeginInvoke(new MethodInvoker(delegate
                        {
                            _updateItem.Enabled = true;
                            _updateItem.Text = Strings.T("Update to ", "Bijwerken naar ") + release.Tag;
                            Balloon(Strings.T("Updating failed: ", "Bijwerken mislukt: ") + ex.Message);
                        }));
                    }
                    catch { }
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void ListenForQuitSignal()
        {
            _invoker = new Control();
            _invoker.CreateControl();
            IntPtr force = _invoker.Handle;   // the handle must exist before BeginInvoke from another thread
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
            ThreadPool.RegisterWaitForSingleObject(_quitSignal, delegate
            {
                try { _invoker.BeginInvoke(new MethodInvoker(delegate { Log.Write("quit requested from outside"); Quit(); })); }
                catch { }
            }, null, Timeout.Infinite, true);
        }

        private void Quit()
        {
            if (_quitting) return;
            _quitting = true;
            Log.Write("quitting");
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            Native.UnregisterHotKey(_window.Handle, HotkeyId);
            _bubbles.Dispose();
            _instance = null;
            _tray.Visible = false;
            _tray.Dispose();
            _window.Dispose();
            ExitThread();
        }
    }

    /// <summary>Invisible window for WM_HOTKEY, shell hook messages and TaskbarCreated.</summary>
    internal class MessageWindow : NativeWindow, IDisposable
    {
        private readonly uint _taskbarCreated;
        private readonly uint _shellHook;

        public event EventHandler HotkeyPressed;
        public event EventHandler TaskbarRecreated;
        public event Action<int, IntPtr> ShellMessage;

        public MessageWindow()
        {
            _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            _shellHook = Native.RegisterWindowMessage("SHELLHOOK");
            CreateHandle(new CreateParams());
            Native.RegisterShellHookWindow(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                if (HotkeyPressed != null) HotkeyPressed(this, EventArgs.Empty);
            }
            else if (_shellHook != 0 && m.Msg == (int)_shellHook)
            {
                if (ShellMessage != null) ShellMessage(m.WParam.ToInt32(), m.LParam);
            }
            else if (_taskbarCreated != 0 && m.Msg == (int)_taskbarCreated)
            {
                if (TaskbarRecreated != null) TaskbarRecreated(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                Native.DeregisterShellHookWindow(Handle);
                DestroyHandle();
            }
        }
    }
}
