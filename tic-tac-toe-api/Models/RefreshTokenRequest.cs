namespace tic_tac_toe_api.Models;

public class RefreshTokenRequest
{
    public string AccessToken { get; set; }

    public string RefreshToken { get; set; }
}