using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Utils;

public static partial class ConversionUtils
{
    public static string GameStateLabel(string state) => state.ToLowerInvariant() switch
    {
        "pause"       => "Paused",
        "play"        => "Running",
        "fastforward" => "Fast Forward",
        "setup"       => "Setup",
        "end"         => "Ended",
        _             => state,
    };

    public static string FormatTimeLeft(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
    
    public static string MonthName(int m) => m switch {
        1 => "January", 2 => "February", 3 => "March",    4 => "April",
        5 => "May",     6 => "June",     7 => "July",     8 => "August",
        9 => "September", 10 => "October", 11 => "November", 12 => "December",
        _ => m.ToString()
    };

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

    // ── JSON parsing helpers ───────────────────────────────────────────────────
    
    /// <summary>
    /// Extracts the payload property from a JSON root element if it exists, otherwise returns the root.
    /// </summary>
    public static JsonElement GetPayload(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("payload", out var payload))
            return payload;
        return root;
    }

    /// <summary>
    /// Gets an integer property from a JSON element by name (case-insensitive).
    /// Returns 0 if the property is not found or cannot be parsed.
    /// </summary>
    public static int GetIntProp(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetInt32();
            if (prop.Value.ValueKind == JsonValueKind.String &&
                int.TryParse(prop.Value.GetString(), out var v)) return v;
        }
        return 0;
    }

    /// <summary>
    /// Gets a string property from a JSON element by name (case-insensitive).
    /// Returns null if the property is not found.
    /// </summary>
    public static string? GetStringProp(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
        }
        return null;
    }

    /// <summary>
    /// Gets a nullable integer property from a JSON element by trying multiple names (case-insensitive).
    /// Returns null if none of the properties are found or cannot be parsed.
    /// </summary>
    public static int? GetNullableIntProp(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetInt32();
                if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out var parsed))
                    return parsed;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets a DateTime property from a JSON element by trying multiple names (case-insensitive).
    /// Supports various date formats including "MMM d HH:mm" and ISO 8601.
    /// Returns null if none of the properties are found or cannot be parsed.
    /// </summary>
    public static DateTime? GetDateTimeProp(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var text = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (DateTime.TryParseExact(
                        text,
                        ["MMM d HH:mm", "MMM dd HH:mm"],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var monthDayTime))
                    {
                        return new DateTime(
                            DateTime.UtcNow.Year,
                            monthDayTime.Month,
                            monthDayTime.Day,
                            monthDayTime.Hour,
                            monthDayTime.Minute,
                            0,
                            DateTimeKind.Utc);
                    }

                    var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

                    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var parsedOffset))
                        return parsedOffset.UtcDateTime;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Gets a string property from a JSON element by trying multiple names with loose matching.
    /// Normalizes property names by removing non-alphanumeric characters and comparing case-insensitively.
    /// Returns null if none of the properties are found.
    /// </summary>
    public static string? GetStringPropLoose(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var normalizedNames = names.Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            if (!normalizedNames.Contains(NormalizeKey(prop.Name))) continue;
            return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
        }

        return null;
    }

    /// <summary>
    /// Normalizes a key by removing non-alphanumeric characters and converting to lowercase.
    /// </summary>
    public static string NormalizeKey(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    /// <summary>
    /// Converts a hex color string to an RGBA int array [r, g, b, a].
    /// </summary>
    public static int[] HexToRgbaArray(string hex)
    {
        var h = hex.TrimStart('#');
        int r = Convert.ToInt32(h[..2], 16);
        int g = Convert.ToInt32(h[2..4], 16);
        int b = Convert.ToInt32(h[4..6], 16);
        int a = h.Length >= 8 ? Convert.ToInt32(h[6..8], 16) : 255;
        return [r, g, b, a];
    }

    /// <summary>
    /// Generates a fallback display name from a layer name by removing leading underscores
    /// and replacing underscores/hyphens with spaces.
    /// </summary>
    public static string FallbackDisplayName(string layerName)
    {
        var s = layerName.TrimStart('_');
        return Regex.Replace(s, "[_\\-]+", " ");
    }

    /// <summary>
    /// Parses the assembly time from a layer JSON element by looking for the ASSEMBLY state.
    /// Returns 0 if not found.
    /// </summary>
    public static int ParseAssemblyTime(JsonElement layer)
    {
        if (!layer.TryGetProperty("layer_states", out var statesEl) ||
            statesEl.ValueKind != JsonValueKind.Array)
            return 0;

        foreach (var s in statesEl.EnumerateArray())
        {
            var stateName = GetStringProp(s, "state");
            if (string.Equals(stateName, "ASSEMBLY", StringComparison.OrdinalIgnoreCase))
                return GetIntProp(s, "time");
        }
        return 0;
    }

    /// <summary>
    /// Parses a plan message from a JSON element.
    /// Returns null if the message text is missing or empty.
    /// </summary>
    public static PlanMessage? ParsePlanMessage(JsonElement pm, int planId, int sequence)
    {
        var messageId = GetStringProp(pm, "message_id")
                     ?? GetStringProp(pm, "id")
                     ?? "";

        var userName = GetStringProp(pm, "user_name")
                    ?? GetStringProp(pm, "username")
                    ?? GetStringProp(pm, "name")
                    ?? "Unknown";

        var message = GetStringProp(pm, "message")
                   ?? GetStringProp(pm, "text")
                   ?? GetStringProp(pm, "body")
                   ?? GetStringProp(pm, "content")
                   ?? pm.ToString();

        var countryId = GetNullableIntProp(pm,
            "team_id",
            "country_id",
            "country",
            "sender_country_id",
            "user_country_id");

        var countryName = GetStringProp(pm, "country_name")
                       ?? GetStringProp(pm, "country_display_name")
                       ?? (countryId.HasValue && countryId.Value > 0 ? countryId.Value.ToString() : "");

        var sentAt = GetDateTimeProp(pm,
            "created_at",
            "created",
            "sent_at",
            "timestamp",
            "time");

        if (string.IsNullOrWhiteSpace(message)) return null;

        return new PlanMessage(messageId, planId, countryId, countryName, userName, message, sentAt, sequence);
    }
}