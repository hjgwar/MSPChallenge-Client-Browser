using System.Text.Json;
using MSPChallenge_Client_Browser.Services;
using MSPChallenge_Client_Browser.Models;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class OnlineUsers : IDisposable
{   
    private List<UserEntry> _users        = [];
    private bool            _usersLoading = true;
    private string?         _usersError = null;

    protected override async Task OnInitializedAsync()
    {
        await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        StateHasChanged();
        try
        {
            var endpoint = "User/List";
            JsonElement apiCallResults = UserSessionService.IsAdmin
                ? await ApiClient.GetAsync(endpoint)
                : await ApiClient.PostFormAsync(endpoint, [new KeyValuePair<string, string>("country_id", UserSessionService.User.CountryId.ToString())]);
            var payload = apiCallResults.TryGetProperty("payload", out var p) ? p : apiCallResults;
            var list = new List<UserEntry>();
            if (payload.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in payload.EnumerateArray())
                {
                    var name = u.TryGetProperty("user_name",       out var n) ? n.GetString() ?? "" : "";
                    var cid  = u.TryGetProperty("user_country_id", out var c)
                        ? (c.ValueKind == JsonValueKind.Number ? c.GetInt32()
                        : int.TryParse(c.GetString(), out var parsed) ? parsed : 0)
                        : 0;
                    var col  = GameSessionState.CountryColours.GetValueOrDefault(cid, "#6c757d");
                    list.Add(new UserEntry(null, name, cid, col));
                }
            }
            _users = UserSessionService.IsAdmin
                ? [.. list.OrderBy(u => u.CountryId).ThenBy(u => u.Name)]
                : [.. list.OrderBy(u => u.Name)];
        }
        catch (Exception ex)
        {
            _usersError = ex.Message;
        }
        finally
        {
            _usersLoading = false;
            StateHasChanged();
        }
    }
    
    private string CurrentUserCountryName()
    {
        return GameSessionState.CountryNames.GetValueOrDefault(UserSessionService.User.CountryId, "Unknown");
    }

    private string CurrentUserCountryColour()
    {
        return GameSessionState.CountryColours.GetValueOrDefault(UserSessionService.User.CountryId, "#6c757d");
    }

    public void Dispose()
    {
        
    }
}