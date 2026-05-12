using System.Text.Json;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Session
{
    private bool isLoading;
    private bool isConnecting;
    private bool configLoaded;
    private string? errorMessage;
    // Key = displayName shown to user, Value = integer country_id sent to API
    private Dictionary<string, int> countries = new();
    private string selectedCountry = string.Empty;
    private string username = string.Empty;
    private string password = string.Empty;
    private bool userAdminHasPassword;
    private bool userCommonHasPassword;

    private static readonly HashSet<string> AdminRoles = new() { "Admin", "Region Manager" };

    private bool NeedsPassword =>
        !string.IsNullOrEmpty(selectedCountry) &&
        (AdminRoles.Contains(selectedCountry) ? userAdminHasPassword : userCommonHasPassword);

    protected override async Task OnInitializedAsync()
    {
        if (SessionState.SessionId == 0 || string.IsNullOrEmpty(SessionState.GameServerAddress))
        {
            NavigationManager.NavigateTo("/");
            return;
        }

        isLoading = true;
        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var root = await ApiClient.GetAsync($"{baseAddress}/{SessionState.SessionId}/api/Game/Config");

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

            if (payload.TryGetProperty("countries", out var countriesEl) &&
                countriesEl.ValueKind == JsonValueKind.String)
            {
                var layerName = countriesEl.GetString()!;
                var metaUrl = $"{baseAddress}/{SessionState.SessionId}/api/Layer/MetaByName";
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
                        if (lt.TryGetProperty("displayName", out var dn) &&
                            lt.TryGetProperty("value", out var val))
                        {
                            countries[dn.GetString() ?? string.Empty] = val.GetInt32();
                        }
                    }
                }

                // Admin = 1, Region Manager = 2 (special sentinel values)
                countries["Admin"] = 1;
                countries["Region Manager"] = 2;
            }

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
            isLoading = false;
        }
    }

    private async Task Connect()
    {
        if (!countries.TryGetValue(selectedCountry, out var countryId))
        {
            errorMessage = "Please select a valid country.";
            return;
        }

        isConnecting = true;
        errorMessage = null;

        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{SessionState.SessionId}/api/User/RequestSession";

            var fields = new List<KeyValuePair<string, string>>
            {
                new("country_id", countryId.ToString()),
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

            SessionState.ApiAccessToken  = accessToken.GetString() ?? string.Empty;
            SessionState.ApiRefreshToken = refreshToken.GetString() ?? string.Empty;
            SessionState.CountryId       = countryId;

            if (payload.TryGetProperty("user_id", out var userIdEl) &&
                userIdEl.ValueKind == JsonValueKind.Number)
                SessionState.UserId = userIdEl.GetInt32();
            else
                SessionState.UserId = countryId; // fallback

            NavigationManager.NavigateTo("/game");
        }
        catch (Exception ex)
        {
            errorMessage = $"An error occurred: {ex.Message}";
        }
        finally
        {
            isConnecting = false;
        }
    }
}
