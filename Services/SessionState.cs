namespace MSPChallenge_Client_Browser.Services;

public class SessionState
{
    public int SessionId { get; set; }
    public int CountryId { get; set; }
    public int UserId { get; set; }
    public string GameServerAddress { get; set; } = string.Empty;
    public string GameWsServerAddress { get; set; } = string.Empty;
    public string ApiAccessToken { get; set; } = string.Empty;
    public string ApiRefreshToken { get; set; } = string.Empty;
}
