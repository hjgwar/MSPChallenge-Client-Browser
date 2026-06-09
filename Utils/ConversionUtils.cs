using System.Text.RegularExpressions;
using System.Globalization;

namespace MSPChallenge_Client_Browser.Utils;

public static partial class ConversionUtils
{
    public static string HexToRGB(string hex, double alpha = 1.0)
    {
        if (string.IsNullOrEmpty(hex) || !hex.StartsWith('#')) return "";
        var h = hex.TrimStart('#');
        if (h.Length < 6) return "";
        int r = Convert.ToInt32(h[..2], 16);
        int g = Convert.ToInt32(h[2..4], 16);
        int b = Convert.ToInt32(h[4..6], 16);
        if (h.Length == 8) alpha = Convert.ToInt32(h[6..8], 16) / 255.0;
        return $"rgba({r}, {g}, {b}, {alpha:F2})";
    }

    public static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var s = MyRegex().Replace(value, " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    [GeneratedRegex("[_\\-]+")]
    private static partial Regex MyRegex();
}