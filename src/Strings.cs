using System.Globalization;

namespace Bubbels
{
    /// <summary>
    /// The interface speaks English, or Dutch when Windows itself is in Dutch.
    /// Every text sits right where it is used, as T("English", "Nederlands").
    /// </summary>
    internal static class Strings
    {
        public static readonly bool Dutch =
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl";

        public static string T(string english, string dutch)
        {
            return Dutch ? dutch : english;
        }
    }
}
