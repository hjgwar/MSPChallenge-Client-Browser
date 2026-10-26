using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Development-only. Logs the data sent with each HttpClient request, and every JSON property of the
/// response as <c>path = value</c>. Long values are truncated, large arrays summarised and
/// secrets (passwords, tokens) redacted, so the output stays readable and safe to share.
/// </summary>
public sealed partial class HttpTrafficLoggingHandler(ILogger<HttpTrafficLoggingHandler> logger) : DelegatingHandler
{
    private const int MaxLoggedValues = 200;
    private const int MaxArrayItems = 5;
    private const int MaxValueLength = 120;
    private const int MaxRawBodyLength = 500;

    [GeneratedRegex("password|token|secret|authorization", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Without the query: its values are listed (and redacted where needed) in the data below.
        var target = $"{request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}";

        logger.LogInformation("Request data for {Target}:\n{Data}", target, await DescribeRequestAsync(request, cancellationToken));

        var stopwatch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        logger.LogInformation("Response data from {Target} - {StatusCode} after {Elapsed:F0}ms ({Length} chars):\n{Data}",
            target, (int)response.StatusCode, stopwatch.Elapsed.TotalMilliseconds, body.Length, DescribeResponseBody(body));

        return response;
    }

    private static async Task<string> DescribeRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        var query = request.RequestUri?.Query;
        if (!string.IsNullOrEmpty(query))
            AppendPairs(lines, "query", HttpUtility.ParseQueryString(query));

        if (request.Content is not null)
        {
            var mediaType = request.Content.Headers.ContentType?.MediaType;
            var text = await request.Content.ReadAsStringAsync(cancellationToken);

            if (string.Equals(mediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                AppendPairs(lines, "form", HttpUtility.ParseQueryString(text));
            else if (!TryAppendJson(lines, text))
                lines.Add($"  body ({mediaType ?? "unknown type"}): {Truncate(text, MaxRawBodyLength)}");
        }

        return lines.Count == 0 ? "  (no data)" : string.Join('\n', lines);
    }

    private static string DescribeResponseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "  (empty body)";

        var lines = new List<string>();
        return TryAppendJson(lines, body)
            ? string.Join('\n', lines)
            : $"  body: {Truncate(body, MaxRawBodyLength)}";
    }

    private static void AppendPairs(List<string> lines, string prefix, System.Collections.Specialized.NameValueCollection pairs)
    {
        foreach (var key in pairs.AllKeys)
        {
            if (key is null) continue;
            var value = SensitiveName().IsMatch(key) ? "***" : $"\"{Truncate(pairs[key] ?? string.Empty, MaxValueLength)}\"";
            lines.Add($"  {prefix}.{key} = {value}");
        }
    }

    private static bool TryAppendJson(List<string> lines, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var flattener = new JsonFlattener();
            flattener.Walk(doc.RootElement, string.Empty, null);
            lines.AddRange(flattener.Lines);
            if (flattener.Omitted > 0)
                lines.Add($"  … {flattener.Omitted} more values not shown");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= maxLength ? value : $"{value[..maxLength]}… ({value.Length} chars)";
    }

    /// <summary>Flattens a JSON document into <c>path = value</c> lines (e.g. <c>payload.simulation_settings[0].simulation_type = "MEL"</c>).</summary>
    private sealed class JsonFlattener
    {
        public List<string> Lines { get; } = [];
        public int Omitted { get; private set; }

        public void Walk(JsonElement element, string path, string? name)
        {
            if (name is not null && SensitiveName().IsMatch(name))
            {
                Emit(path, "***");
                return;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var any = false;
                    foreach (var property in element.EnumerateObject())
                    {
                        any = true;
                        Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", property.Name);
                    }
                    if (!any) Emit(path, "{}");
                    break;

                case JsonValueKind.Array:
                    var count = element.GetArrayLength();
                    if (count == 0) Emit(path, "[]");
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (index >= MaxArrayItems) break;
                        Walk(item, $"{path}[{index}]", null);
                        index++;
                    }
                    if (count > MaxArrayItems)
                        Emit($"{path}.Length", $"{count} (only the first {MaxArrayItems} items are shown)");
                    break;

                case JsonValueKind.String:
                    Emit(path, $"\"{Truncate(element.GetString() ?? string.Empty, MaxValueLength)}\"");
                    break;

                case JsonValueKind.Number:
                    Emit(path, element.GetRawText());
                    break;

                case JsonValueKind.True:
                    Emit(path, "true");
                    break;

                case JsonValueKind.False:
                    Emit(path, "false");
                    break;

                default:
                    Emit(path, "null");
                    break;
            }
        }

        private void Emit(string path, string value)
        {
            if (Lines.Count >= MaxLoggedValues)
            {
                Omitted++;
                return;
            }
            Lines.Add($"  {(path.Length == 0 ? "(root)" : path)} = {value}");
        }
    }
}
