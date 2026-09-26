using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bubbels
{
    /// <summary>
    /// Asks GitHub for the latest release. A newer installed copy updates itself by running
    /// the new setup silently: the setup asks this process to quit (which gives every bubbled
    /// window back), installs, and starts the new version.
    /// </summary>
    internal static class Updater
    {
        private const string LatestApi = "https://api.github.com/repos/gsleraar-hue/bubbels/releases/latest";
        public const string ReleasesPage = "https://github.com/gsleraar-hue/bubbels/releases/latest";

        public class Release
        {
            public Version Version;
            public string Tag;
            public string SetupUrl;
        }

        public static Version Current
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        /// <summary>Installed through the setup (as opposed to the portable exe)?</summary>
        public static bool IsInstalled
        {
            get
            {
                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Bubbels");
                return string.Equals(Path.GetDirectoryName(Path.GetFullPath(System.Windows.Forms.Application.ExecutablePath)),
                                     installDir, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Returns the latest release if it is newer than this build, otherwise null. Blocks.</summary>
        public static Release FindNewer()
        {
            string json = Download(LatestApi);
            Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
            if (!tag.Success) return null;

            var release = new Release { Tag = tag.Groups[1].Value, Version = new Version(tag.Groups[1].Value) };
            Match setup = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+-setup\\.exe)\"");
            if (setup.Success) release.SetupUrl = setup.Groups[1].Value;

            return Normalize(release.Version) > Normalize(Current) ? release : null;
        }

        /// <summary>Download the setup and run it silently. Blocks until the download is done.</summary>
        public static void Install(Release release)
        {
            if (release.SetupUrl == null) throw new InvalidOperationException("no setup in this release");
            string file = Path.Combine(Path.GetTempPath(), "Bubbels-" + release.Tag + "-setup.exe");
            using (var client = NewClient()) client.DownloadFile(release.SetupUrl, file);
            if (new FileInfo(file).Length < 20 * 1024) throw new InvalidOperationException("download too small");

            Log.Write("update: starting setup {0}", release.Tag);
            // Keep the user's current autostart choice.
            string args = "/silent" + (Autostart.IsOn() ? "" : " /no-autostart");
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
        }

        private static string Download(string url)
        {
            using (var client = NewClient()) return client.DownloadString(url);
        }

        private static WebClient NewClient()
        {
            // .NET Framework 4 does not offer TLS 1.2 by default; GitHub requires it.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "Bubbels/" + Current;
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return client;
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }
    }
}
