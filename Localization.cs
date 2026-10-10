using System.Globalization;

namespace WOMicNext;

internal static class Localization
{
    public static bool English { get; set; }
    public static string T(string chinese, string english) => English ? english : chinese;
    public static string F(string chinese, string english, params object[] values) =>
        string.Format(English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN"),
            English ? english : chinese, values);
}
