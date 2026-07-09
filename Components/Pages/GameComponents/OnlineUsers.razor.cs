using System.Text.Json;
using MSPChallenge_Client_Browser.Services;
using MSPChallenge_Client_Browser.Models;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class OnlineUsers : IDisposable
{   
    private List<User> _users        = [];
    private bool _usersLoading = true;
    private string? _usersError = null;

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
                : await ApiClient.PostFormAsync(endpoint, [new KeyValuePair<string, string>("country_id", UserSessionService.User.Country.Id.ToString())]);
            var payload = apiCallResults.TryGetProperty("payload", out var p) ? p : apiCallResults;
            var list = new List<User>();
            if (payload.ValueKind == JsonValueKind.Array)
            {
                int i = 1;
                foreach (var u in payload.EnumerateArray())
                {
                    var name = u.TryGetProperty("user_name",       out var n) ? n.GetString() ?? "" : "";
                    var cid  = u.TryGetProperty("user_country_id", out var c)
                        ? (c.ValueKind == JsonValueKind.Number ? c.GetInt32()
                        : int.TryParse(c.GetString(), out var parsed) ? parsed : 0)
                        : 0;
                    var col  = GameSessionState.Countries.FirstOrDefault(c => c.Id == cid, new Country(cid, "Unknown", "#6c757d")).Color;
                    list.Add(new User(i, name, new Country(cid, "Unknown", col)));
                    i++;
                }
            }
            _users = UserSessionService.IsAdmin
                ? [.. list.OrderBy(u => u.Country.Id).ThenBy(u => u.Name)]
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
        return GameSessionState.Countries.FirstOrDefault(c => c.Id == UserSessionService.User.Country.Id, new Country(UserSessionService.User.Country.Id, "Unknown", "#6c757d")).Name;
    }

    private string CurrentUserCountryColour()
    {
        return GameSessionState.Countries.FirstOrDefault(c => c.Id == UserSessionService.User.Country.Id, new Country(UserSessionService.User.Country.Id, "Unknown", "#6c757d")).Color;
    }

    public void Dispose()
    {
        
    }
}