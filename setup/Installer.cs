using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BubbelsSetup
{
    /// <summary>Everything the setup actually does. The window is only a front for this.</summary>
    internal static class Installer
    {
        // Per user, so nothing here ever needs administrator rights.
        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Bubbels");

        public static readonly string ExePath = Path.Combine(InstallDir, "Bubbels.exe");
        public static readonly string SetupCopy = Path.Combine(InstallDir, "Bubbels-setup.exe");

        public static readonly string StartMenuLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Bubbels.lnk");

        public static readonly string DesktopLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Bubbels.lnk");

        public static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bubbels");

        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Bubbels";
        public const string Version = "1.1.0";

        public static bool IsInstalled() { return File.Exists(ExePath); }

        public static bool IsAutostartOn() { return Bubbels.Autostart.IsOn(); }

        // ----------------------------------------------------------------- install

        public static void Install(bool autostart, bool desktopShortcut, bool launch)
        {
            StopRunningApp();

            Directory.CreateDirectory(InstallDir);
            ExtractResource("Bubbels.exe", ExePath);

            // Keep a copy so "Installed apps" has something to call for removal.
            if (!PathsEqual(Application.ExecutablePath, SetupCopy))
                File.Copy(Application.ExecutablePath, SetupCopy, true);

            CreateShortcut(StartMenuLink, ExePath, Bubbels.Strings.T("Put any window in a floating bubble", "Stop elk venster in een zwevende bubbel"));
            if (desktopShortcut) CreateShortcut(DesktopLink, ExePath, "Bubbels");
            else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

            SetAutostart(autostart);
            WriteUninstallEntry();

            if (launch)
            {
                Process.Start(ExePath);
                // The tray entry only exists once the app has shown its icon, so pin it
                // afterwards -- otherwise Windows 11 hides it behind the chevron and it
                // looks as if nothing started at all.
                Thread.Sleep(2000);
                PromoteTrayIcon();
            }
        }

        public static void Uninstall()
        {
            StopRunningApp();
            SetAutostart(false);

            foreach (string link in new[] { StartMenuLink, DesktopLink })
                if (File.Exists(link)) File.Delete(link);

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (Directory.Exists(SettingsDir)) Directory.Delete(SettingsDir, true);

            // The setup may be running from inside the folder it has to delete, so hand
            // the last step to a throwaway shell that outlives this process.
            ScheduleFolderDeletion(InstallDir);
        }

        // ------------------------------------------------------------------- pieces

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static void StopRunningApp()
        {
            Process[] running = Process.GetProcessesByName("Bubbels");
            if (running.Length == 0) return;

            // Ask politely first: Bubbels then gives every bubbled window back before it
            // exits. Killing it outright would leave those windows hidden until next start.
            try
            {
                EventWaitHandle signal;
                // Same name as TrayContext.QuitSignalName in the app.
                if (EventWaitHandle.TryOpenExisting(@"Local\Bubbels.Quit", out signal))
                    using (signal) signal.Set();
            }
            catch { }

            foreach (Process p in running)
            {
                try
                {
                    p.WaitForExit(4000);
                    if (!p.HasExited) p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
            }
            Thread.Sleep(300);
        }

        private static void ExtractResource(string name, string target)
        {
            Assembly self = Assembly.GetExecutingAssembly();
            using (Stream source = self.GetManifestResourceStream(name))
            {
                if (source == null) throw new InvalidOperationException("Bubbels.exe is missing from this setup.");
                using (var file = new FileStream(target, FileMode.Create, FileAccess.Write))
                    source.CopyTo(file);
            }
        }

        /// <summary>Via the Windows Script Host, so the setup needs no extra library.</summary>
        private static void CreateShortcut(string linkPath, string target, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                                                     null, shell, new object[] { linkPath });
                Type linkType = link.GetType();
                Set(linkType, link, "TargetPath", target);
                Set(linkType, link, "WorkingDirectory", Path.GetDirectoryName(target));
                Set(linkType, link, "Description", description);
                Set(linkType, link, "IconLocation", target + ",0");
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
        }

        private static void Set(Type type, object instance, string property, object value)
        {
            type.InvokeMember(property, BindingFlags.SetProperty, null, instance, new[] { value });
        }

        public static void SetAutostart(bool on)
        {
            Bubbels.Autostart.Set(on, ExePath);
        }

        /// <summary>Show the tray icon on the taskbar instead of inside the overflow flyout.</summary>
        public static void PromoteTrayIcon()
        {
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (RegistryKey entry = root.OpenSubKey(name, true))
                        {
                            if (entry == null) continue;
                            var path = entry.GetValue("ExecutablePath") as string;
                            if (path == null || path.IndexOf("Bubbels", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        }
                    }
                }
            }
            catch { /* only cosmetic */ }
        }

        private static void WriteUninstallEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                if (key == null) return;
                key.SetValue("DisplayName", "Bubbels");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "");
                key.SetValue("DisplayIcon", ExePath + ",0");
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", "\"" + SetupCopy + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", 120, RegistryValueKind.DWord);
            }
        }

        private static void ScheduleFolderDeletion(string folder)
        {
            var start = new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + folder + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(start);
        }
    }
}
