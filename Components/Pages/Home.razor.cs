using System.Text.Json;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages;

public partial class Home
{
    private string serverAddress = "server.mspchallenge.info";
    private bool isLoading;
    private string? errorMessage;
    private string? serverDescription;
    private List<JsonElement>? sessions;

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
                host = "https://" + host;
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

    private void ConnectToSession(JsonElement session)
    {
        SessionState.SessionId = session.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        SessionState.GameServerAddress = GetString(session, "game_server_address");
        NavigationManager.NavigateTo("/session");
    }

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
