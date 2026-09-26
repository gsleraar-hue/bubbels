using System;
using System.IO;
using System.Text;

namespace Bubbels
{
    /// <summary>
    /// A few lines in a text file. Exists because a tray app that fails at logon
    /// leaves no other trace: Windows logs nothing and there is no window to show it.
    /// </summary>
    internal static class Log
    {
        private static readonly object Lock = new object();

        public static string FilePath { get { return Path.Combine(Program.DataFolder, "bubbels.log"); } }

        public static void Write(string line)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Program.DataFolder);
                    Trim();
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { /* logging must never be the thing that breaks the app */ }
        }

        public static void Write(string format, params object[] args)
        {
            Write(string.Format(format, args));
        }

        public static void Exception(string where, Exception ex)
        {
            if (ex == null) { Write(where + ": onbekende fout"); return; }
            Write("{0}: {1}: {2}{3}{4}", where, ex.GetType().Name, ex.Message,
                  Environment.NewLine, ex.StackTrace);
        }

        private static void Trim()
        {
            try
            {
                var file = new FileInfo(FilePath);
                if (!file.Exists || file.Length < 200 * 1024) return;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                int keep = lines.Length / 2;
                File.WriteAllLines(FilePath, SubArray(lines, lines.Length - keep, keep), Encoding.UTF8);
            }
            catch { }
        }

        private static string[] SubArray(string[] source, int start, int count)
        {
            var result = new string[count];
            Array.Copy(source, start, result, 0, count);
            return result;
        }
    }
}
