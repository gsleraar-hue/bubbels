using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Bubbels
{
    /// <summary>
    /// Starting with Windows is a scheduled task, not a Run key. A Run entry depends on
    /// Explorer walking its startup list, which waits about thirty seconds per item and
    /// silently skips whatever it does not care for; the task fires ten seconds after
    /// logon and answers to nobody. Shared by the app and the setup.
    /// </summary>
    internal static class Autostart
    {
        public const string TaskName = "Bubbels";
        private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        public static bool IsOn()
        {
            int exitCode;
            Run("schtasks.exe", "/query /tn \"" + TaskName + "\"", out exitCode);
            return exitCode == 0;
        }

        public static void Set(bool on, string exePath)
        {
            RemoveLegacyRunEntry();

            if (!on)
            {
                int ignored;
                Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f", out ignored);
                return;
            }

            string xmlFile = Path.Combine(Path.GetTempPath(), "bubbels-task.xml");
            // schtasks wants UTF-16 for /xml.
            File.WriteAllText(xmlFile, BuildTaskXml(exePath), Encoding.Unicode);
            try
            {
                int exitCode;
                Run("schtasks.exe",
                    "/create /tn \"" + TaskName + "\" /xml \"" + xmlFile + "\" /f", out exitCode);
                if (exitCode != 0)
                    throw new InvalidOperationException("schtasks returned error code " + exitCode);
            }
            finally
            {
                try { File.Delete(xmlFile); } catch { }
            }
        }

        /// <summary>The Run key was the old mechanism; leave nothing of it behind.</summary>
        private static void RemoveLegacyRunEntry()
        {
            foreach (string path in new[] { LegacyRunKey, LegacyApprovedKey })
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true))
                        if (key != null) key.DeleteValue(TaskName, false);
                }
                catch { }
            }
        }

        private static string BuildTaskXml(string exePath)
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;

            return
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo>\r\n" +
"    <Description>Start Bubbels at sign-in</Description>\r\n" +
"    <URI>\\" + TaskName + "</URI>\r\n" +
"  </RegistrationInfo>\r\n" +
"  <Triggers>\r\n" +
"    <LogonTrigger>\r\n" +
"      <Enabled>true</Enabled>\r\n" +
"      <UserId>" + Escape(user) + "</UserId>\r\n" +
"      <Delay>PT10S</Delay>\r\n" +
"    </LogonTrigger>\r\n" +
"  </Triggers>\r\n" +
"  <Principals>\r\n" +
"    <Principal id=\"Author\">\r\n" +
"      <UserId>" + Escape(user) + "</UserId>\r\n" +
"      <LogonType>InteractiveToken</LogonType>\r\n" +
"      <RunLevel>LeastPrivilege</RunLevel>\r\n" +
"    </Principal>\r\n" +
"  </Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
// A laptop on battery should still get its bubbles.
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>false</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
"    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>\r\n" +
"    <WakeToRun>false</WakeToRun>\r\n" +
// It is a tray app: it is supposed to keep running.
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\">\r\n" +
"    <Exec>\r\n" +
"      <Command>" + Escape(exePath) + "</Command>\r\n" +
"    </Exec>\r\n" +
"  </Actions>\r\n" +
"</Task>\r\n";
        }

        private static string Escape(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string Run(string file, string arguments, out int exitCode)
        {
            var start = new ProcessStartInfo(file, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(15000);
                exitCode = process.HasExited ? process.ExitCode : -1;
                return output;
            }
        }
    }
}
