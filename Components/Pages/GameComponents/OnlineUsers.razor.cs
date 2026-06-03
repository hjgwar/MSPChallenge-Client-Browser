using System.Text.Json;
using MSPChallenge_Client_Browser.Services;
using MSPChallenge_Client_Browser.Models;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class OnlineUsers : IDisposable
{   
    private List<UserEntry> _users        = [];
    private bool            _usersLoading;
    private string?         _usersError;

    protected override async Task OnInitializedAsync()
    {
        await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        _usersLoading = true;
        _usersError = null;
        StateHasChanged();
        try
        {
            var baseAddress = SessionState.GameServerAddress.TrimEnd('/');
            var url = $"{baseAddress}/{SessionState.SessionId}/api/User/List";
            bool isAdmin = SessionState.CountryId == 1 || SessionState.CountryId == 2;
            JsonElement root = isAdmin
                ? await ApiClient.GetAsync(url)
                : await ApiClient.PostFormAsync(url, new[] { new KeyValuePair<string, string>("country_id", SessionState.CountryId.ToString()) });
            var payload = root.TryGetProperty("payload", out var p) ? p : root;
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
                    list.Add(new UserEntry(name, cid, col));
                }
            }
            _users = isAdmin
                ? list.OrderBy(u => u.CountryId).ThenBy(u => u.Name).ToList()
                : list.OrderBy(u => u.Name).ToList();
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

    public void Dispose()
    {
        
    }
}