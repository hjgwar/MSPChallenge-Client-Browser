using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Services;

public class UserSessionService
{
    public int SessionId { get; set; } 
    public string GameServerAddress { get; set; } = string.Empty;
    public string GameWsServerAddress { get; set; } = string.Empty;
    public string ApiAccessToken { get; set; } = string.Empty;
    public string ApiRefreshToken { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public bool IsAdmin => User.Country.Id == 1 || User.Country.Id == 2;
}
