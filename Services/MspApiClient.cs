using System.Net.Http.Headers;
using System.Text.Json;

namespace MSPChallenge_Client_Browser.Services;

/// <summary>
/// Thin wrapper around HttpClient for MSP Challenge API calls.
/// Automatically attaches Msp-Client-Version and, when available, the Bearer token.
/// </summary>
public class MspApiClient
{
    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration _configuration;
    private readonly SessionState _sessionState;

    public MspApiClient(IHttpClientFactory factory, IConfiguration configuration, SessionState sessionState)
    {
        _factory = factory;
        _configuration = configuration;
        _sessionState = sessionState;
    }

    /// <summary>GET a URL and return the parsed JSON root element.</summary>
    public async Task<JsonElement> GetAsync(string url)
    {
        var client = CreateClient();
        var response = await client.GetAsync(url);
        return await ReadJsonAsync(response);
    }

    /// <summary>POST form-encoded key/value pairs and return the parsed JSON root element.</summary>
    public async Task<JsonElement> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var client = CreateClient();
        var response = await client.PostAsync(url, new FormUrlEncodedContent(fields));
        return await ReadJsonAsync(response);
    }

    /// <summary>POST a JSON-serialisable object and return the parsed JSON root element.</summary>
    public async Task<JsonElement> PostJsonAsync(string url, object body)
    {
        var client = CreateClient();
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        return await ReadJsonAsync(response);
    }

    /// <summary>Set the game simulation state (PLAY, PAUSE, FASTFORWARD).</summary>
    /// <summary>Set the game simulation state (PLAY, PAUSE, FASTFORWARD).</summary>
    public async Task<JsonElement> SetGameStateAsync(string baseUrl, int sessionId, string state)
    {
        var baseAddress = baseUrl.TrimEnd('/');
        var url = $"{baseAddress}/{sessionId}/api/Game/State";
        var fields = new[] { new KeyValuePair<string, string>("state", state) };
        return await PostFormAsync(url, fields);
    }

    /// <summary>Set current era real time (seconds remaining in current era).</summary>
    public async Task<JsonElement> SetRealtimeAsync(string baseUrl, int sessionId, int realtimeSeconds)
    {
        var baseAddress = baseUrl.TrimEnd('/');
        var url = $"{baseAddress}/{sessionId}/api/Game/Realtime";
        var fields = new[] { new KeyValuePair<string, string>("realtime", realtimeSeconds.ToString()) };
        return await PostFormAsync(url, fields);
    }

    /// <summary>Set future era real times (comma-separated seconds for all 4 eras).</summary>
    public async Task<JsonElement> SetFutureRealtimeAsync(string baseUrl, int sessionId, string realtimeCommaSeparated)
    {
        var baseAddress = baseUrl.TrimEnd('/');
        var url = $"{baseAddress}/{sessionId}/api/Game/FutureRealtime";
        var fields = new[] { new KeyValuePair<string, string>("realtime", realtimeCommaSeparated) };
        return await PostFormAsync(url, fields);
    }
    }

    // -------------------------------------------------------------------------

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        var clientVersion = _configuration["MspClientVersion"] ?? "6.0.0";
        client.DefaultRequestHeaders.TryAddWithoutValidation("Msp-Client-Version", clientVersion);

        if (!string.IsNullOrEmpty(_sessionState.ApiAccessToken))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _sessionState.ApiAccessToken);

        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        // Surface server-side error messages before throwing
        if (!response.IsSuccessStatusCode)
        {
            string? serverMessage = null;
            try
            {
                using var errDoc = JsonDocument.Parse(json);
                if (errDoc.RootElement.TryGetProperty("message", out var m))
                    serverMessage = m.GetString();
            }
            catch { /* ignore parse errors on error bodies */ }

            throw new MspApiException(
                serverMessage ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                (int)response.StatusCode);
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>Thrown when the MSP API returns a non-success status or a success:false payload.</summary>
public class MspApiException : Exception
{
    public int StatusCode { get; }
    public MspApiException(string message, int statusCode = 0) : base(message)
        => StatusCode = statusCode;
}
