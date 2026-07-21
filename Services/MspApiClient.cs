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
    private readonly UserSessionService _userSessionService;
    private HttpClient? _client;

    public MspApiClient(IHttpClientFactory factory, IConfiguration configuration, UserSessionService userSessionService)
    {
        _factory = factory;
        _configuration = configuration;
        _userSessionService = userSessionService;
    }

    /// <summary>GET a URL and return the parsed JSON root element.</summary>
    public async Task<JsonElement> GetAsync(string endPoint )
    {
        CreateClient();
        var url = IsAbsoluteUrl(endPoint) ? endPoint : CompleteApiUrl(endPoint);
        var response = await _client.GetAsync(url);
        return await ReadJsonAsync(response);
    }

    /// <summary>POST form-encoded key/value pairs and return the parsed JSON root element.</summary>
    public async Task<JsonElement> PostFormAsync(string endPoint, IEnumerable<KeyValuePair<string, string>> fields)
    {
        CreateClient();
        var url = IsAbsoluteUrl(endPoint) ? endPoint : CompleteApiUrl(endPoint);
        var response = await _client.PostAsync(url, new FormUrlEncodedContent(fields));
        return await ReadJsonAsync(response);
    }

    /// <summary>POST a JSON-serialisable object and return the parsed JSON root element.</summary>
    public async Task<JsonElement> PostJsonAsync(string endPoint, object body)
    {
        CreateClient();
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var url = IsAbsoluteUrl(endPoint) ? endPoint : CompleteApiUrl(endPoint);
        var response = await _client.PostAsync(url, content);
        return await ReadJsonAsync(response);
    }

    // -------------------------------------------------------------------------
    private static bool IsAbsoluteUrl(string url)
    {
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private string CompleteApiUrl(string endPoint)
    {
        return $"{_userSessionService.GameServerAddress.TrimEnd('/')}/{_userSessionService.SessionId}/api/{endPoint.TrimStart('/')}";
    }

    private void CreateClient()
    {
        if (_client is not null)
        {
            if (_client.DefaultRequestHeaders.Authorization is not null) return;
            if (!string.IsNullOrEmpty(_userSessionService.ApiAccessToken))
            {
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer", _userSessionService.ApiAccessToken
                );
            }
            return;
        }
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Msp-Client-Version", _configuration["MspClientVersion"] ?? "6.0.0"
        );
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

    public async Task LogOff()
    {
        await PostFormAsync("User/CloseSession", new[]
        {
            new KeyValuePair<string, string>("session_id", _userSessionService.SessionId.ToString())
        });
        _client = null;
    }
}

/// <summary>Thrown when the MSP API returns a non-success status or a success:false payload.</summary>
public class MspApiException : Exception
{
    public int StatusCode { get; }
    public MspApiException(string message, int statusCode = 0) : base(message)
        => StatusCode = statusCode;
}
