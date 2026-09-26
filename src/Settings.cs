using System;
using System.IO;
using System.Text;

namespace Bubbels
{
    /// <summary>A few name=value lines in %APPDATA%\Bubbels\settings.ini.</summary>
    internal static class Settings
    {
        private static string FilePath { get { return Path.Combine(Program.DataFolder, "settings.ini"); } }

        public static bool AlwaysOnTop
        {
            get { return Read("alwaysOnTop", "1") == "1"; }
            set { Write("alwaysOnTop", value ? "1" : "0"); }
        }

        private static string Read(string name, string fallback)
        {
            try
            {
                if (!File.Exists(FilePath)) return fallback;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int split = line.IndexOf('=');
                    if (split > 0 && line.Substring(0, split).Trim() == name) return line.Substring(split + 1).Trim();
                }
            }
            catch { }
            return fallback;
        }

        private static void Write(string name, string value)
        {
            try
            {
                var lines = new StringBuilder();
                bool found = false;
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int split = line.IndexOf('=');
                        if (split > 0 && line.Substring(0, split).Trim() == name) { lines.AppendLine(name + "=" + value); found = true; }
                        else if (line.Trim().Length > 0) lines.AppendLine(line);
                    }
                if (!found) lines.AppendLine(name + "=" + value);
                Directory.CreateDirectory(Program.DataFolder);
                File.WriteAllText(FilePath, lines.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Exception("saving settings", ex); }
        }
    }
}
