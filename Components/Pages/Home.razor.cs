using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Home
{
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private MspApiClient ApiClient { get; set; } = null!;
    [Inject] private UserSessionService UserSessionService { get; set; } = null!;
    [Inject] private GameSessionService GameSessionService { get; set; } = null!;

    // ── Server & session selection state ──
    private string serverAddress = "server.mspchallenge.info";
    private bool isLoading;
    private string? errorMessage;
    private string? serverDescription;
    private List<JsonElement>? sessions;

    // ── Join form state (shown after selecting a session) ──
    private JsonElement? selectedSession;
    private bool isLoadingConfig;
    private bool isConnecting;
    private bool configLoaded;
    // Full Country objects (id + name + color), not including Admin/RegionManager sentinels.
    private IReadOnlyList<Country> countries = [];
    private string selectedCountryId = string.Empty;   // string because <select> binds strings
    private string username = string.Empty;
    private string password = string.Empty;
    private bool userAdminHasPassword;
    private bool userCommonHasPassword;

    // Admin=1, RegionManager=2 use the admin password; all real country IDs use the common one.
    private bool NeedsPassword =>
        int.TryParse(selectedCountryId, out var id) && id > 0 &&
        (id <= 2 ? userAdminHasPassword : userCommonHasPassword);

    protected override async Task OnInitializedAsync()
    {
        await FetchGameListAsync();
    }

    private async Task FetchGameListAsync()
    {
        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            errorMessage = "Please enter a server address.";
            return;
        }

        isLoading = true;
        errorMessage = null;
        serverDescription = null;
        sessions = null;

        try
        {
            var host = serverAddress.Trim().TrimEnd('/');
            if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Default to plain HTTP for localhost/loopback, HTTPS for everything else.
                var hostOnly = host.Split(':')[0];
                var isLocal = hostOnly.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                           || hostOnly == "127.0.0.1"
                           || hostOnly == "::1";
                host = (isLocal ? "http://" : "https://") + host;
            }

            var root = await ApiClient.GetAsync($"{host}/manager/gamelist");

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Unexpected response format from server.");
            }

            if (payload.TryGetProperty("server_description", out var desc))
                serverDescription = desc.GetString();

            if (!payload.TryGetProperty("sessionslist", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("No session list found in server response.");
            }

            sessions = list.EnumerateArray().ToList();
        }
        catch (MspApiException ex)
        {
            errorMessage = $"Could not reach the server: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            errorMessage = $"Could not reach the server: {ex.Message}";
        }
        catch (JsonException)
        {
            errorMessage = "The server returned an unexpected response format.";
        }
        catch (Exception ex)
        {
            errorMessage = $"An error occurred: {ex.Message}";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task SelectSessionAsync(JsonElement session)
    {
        selectedSession = session;
        UserSessionService.SessionId = session.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        UserSessionService.GameServerAddress   = ResolveDockerHost(GetString(session, "game_server_address"));
        UserSessionService.GameWsServerAddress = ResolveDockerHost(GetString(session, "game_ws_server_address"));

        // Load session config (countries, password requirements)
        await LoadSessionConfigAsync();
    }

    private async Task LoadSessionConfigAsync()
    {
        isLoadingConfig = true;
        errorMessage = null;
        configLoaded = false;
        countries = [];
        selectedCountryId = string.Empty;
        username = string.Empty;
        password = string.Empty;

        try
        {
            var baseAddress = UserSessionService.GameServerAddress.TrimEnd('/');
            var root = await ApiClient.GetAsync($"{baseAddress}/{UserSessionService.SessionId}/api/Game/Config");

            var payload = root.ValueKind == JsonValueKind.Object &&
                          root.TryGetProperty("payload", out var p)
                ? p
                : root;

            if (payload.TryGetProperty("user_admin_has_password", out var adminPwd))
                userAdminHasPassword = adminPwd.ValueKind == JsonValueKind.True ||
                    (adminPwd.ValueKind == JsonValueKind.Number && adminPwd.GetInt32() != 0);
            if (payload.TryGetProperty("user_common_has_password", out var commonPwd))
                userCommonHasPassword = commonPwd.ValueKind == JsonValueKind.True ||
                    (commonPwd.ValueKind == JsonValueKind.Number && commonPwd.GetInt32() != 0);

            var parsedCountries = new List<Country>();
            if (payload.TryGetProperty("countries", out var countriesEl) &&
                countriesEl.ValueKind == JsonValueKind.String)
            {
                var layerName = countriesEl.GetString()!;
                var metaUrl = $"{baseAddress}/{UserSessionService.SessionId}/api/Layer/MetaByName";
                var metaRoot = await ApiClient.PostFormAsync(metaUrl, new[]
                {
                    new KeyValuePair<string, string>("name", layerName)
                });

                var metaPayload = metaRoot.ValueKind == JsonValueKind.Object &&
                                  metaRoot.TryGetProperty("payload", out var mp)
                    ? mp
                    : metaRoot;

                if (metaPayload.TryGetProperty("layer_type", out var layerTypes) &&
                    layerTypes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lt in layerTypes.EnumerateArray())
                    {
                        if (lt.TryGetProperty("value", out var val) &&
                            val.ValueKind == JsonValueKind.Number &&
                            lt.TryGetProperty("displayName", out var dn))
                        {
                            var color = lt.TryGetProperty("polygonColor", out var col)
                                ? col.GetString() ?? string.Empty
                                : string.Empty;
                            parsedCountries.Add(new Country(
                                Id:    val.GetInt32(),
                                Name:  dn.GetString() ?? string.Empty,
                                Color: color));
                        }
                    }
                }

                // Capture the EEZ layer ID so GameSessionService can load EEZ geometry
                // using Layer/Get without calling Layer/MetaByName a second time.
                if (metaPayload.TryGetProperty("layer_id", out var lIdEl))
                    GameSessionService.EezLayerId = lIdEl.ValueKind == JsonValueKind.Number
                        ? lIdEl.GetInt32().ToString()
                        : lIdEl.GetString();
            }
            // Admin and Region Manager are login roles, not real map countries.
            // They are added as static dropdown options in Home.razor, not here.
            countries = parsedCountries;

            configLoaded = true;
        }
        catch (MspApiException ex)
        {
            errorMessage = $"Could not reach the server: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            errorMessage = $"Could not reach the server: {ex.Message}";
        }
        catch (JsonException)
        {
            errorMessage = "The server returned an unexpected response format.";
        }
        catch (Exception ex)
        {
            errorMessage = $"An error occurred: {ex.Message}";
        }
        finally
        {
            isLoadingConfig = false;
        }
    }

    private async Task ConnectAsync()
    {
        if (!int.TryParse(selectedCountryId, out var countryId) || countryId <= 0)
        {
            errorMessage = "Please select a valid country.";
            return;
        }

        // Look up the full Country object. Real countries are in the list; Admin (1) and
        // Region Manager (2) are login roles with sentinel IDs not stored in the list.
        var selectedCountryObj = countries.FirstOrDefault(c => c.Id == countryId);
        if (selectedCountryObj is null)
        {
            if      (countryId == 1) selectedCountryObj = new Country(1, "Admin",          "#FF69B4");
            else if (countryId == 2) selectedCountryObj = new Country(2, "Region Manager", "#FF69B4");
            else
            {
                errorMessage = "Please select a valid country.";
                return;
            }
        }

        isConnecting = true;
        errorMessage = null;

        try
        {
            var baseAddress = UserSessionService.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{UserSessionService.SessionId}/api/User/RequestSession";

            var fields = new List<KeyValuePair<string, string>>
            {
                new("country_id", selectedCountryObj.Id.ToString()),
                new("user_name", username)
            };
            if (NeedsPassword && !string.IsNullOrEmpty(password))
                fields.Add(new("country_password", password));

            var root = await ApiClient.PostFormAsync(url, fields);

            var payload = root.TryGetProperty("payload", out var p) ? p : root;

            if (!payload.TryGetProperty("api_access_token", out var accessToken) ||
                !payload.TryGetProperty("api_refresh_token", out var refreshToken))
            {
                errorMessage = "Server did not return authentication tokens.";
                return;
            }

            UserSessionService.ApiAccessToken  = accessToken.GetString() ?? string.Empty;
            UserSessionService.ApiRefreshToken = refreshToken.GetString() ?? string.Empty;
            UserSessionService.User = new User(
                Id: payload.TryGetProperty("session_id", out var userIdEl)
                    ? (userIdEl.ValueKind == JsonValueKind.Number
                        ? userIdEl.GetInt32()
                        : int.TryParse(userIdEl.GetString(), out var parsedId) ? parsedId : 0)
                    : 0,
                Name: username,
                Country: selectedCountryObj
            );

            // Seed the pre-built country list (with colors) into GameSessionService so the Game
            // page does not need to re-fetch country metadata from the server.
            GameSessionService.Countries = countries;

            NavigationManager.NavigateTo("/game");
        }
        catch (MspApiException ex)
        {
            errorMessage = FirstLine(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            errorMessage = $"Could not reach the server: {FirstLine(ex.Message)}";
        }
        catch (Exception ex)
        {
            errorMessage = $"An error occurred: {FirstLine(ex.Message)}";
        }
        finally
        {
            isConnecting = false;
        }
    }

    private void CancelJoin()
    {
        selectedSession = null;
        configLoaded = false;
        countries = [];
        selectedCountryId = string.Empty;
        username = string.Empty;
        password = string.Empty;
    }

    /// <summary>Returns only the first non-empty line of a (possibly multi-line) message.</summary>
    private static string FirstLine(string message) =>
        message.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
               .FirstOrDefault()
               ?.Trim() ?? message;

    /// <summary>
    /// Replaces the Docker-internal host alias with localhost so that addresses
    /// advertised by a containerised game server are reachable from the Windows host.
    /// </summary>
    private static string ResolveDockerHost(string address) =>
        address.Replace("host.docker.internal", "localhost", StringComparison.OrdinalIgnoreCase);

    private static string GetString(JsonElement element, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            if (element.TryGetProperty(name, out var prop))
                return prop.ToString();
        }
        return string.Empty;
    }
}
