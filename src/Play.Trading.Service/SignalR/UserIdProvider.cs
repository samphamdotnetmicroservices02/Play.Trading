using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Play.Trading.Service.SignalR;

public class UserIdProvider : IUserIdProvider
{
    public string GetUserId(HubConnectionContext connection)
    {
        // by doing this, you're telling SignalR that in order to identiy any of the uers, it just have to look for their sub claim.
        return connection.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }
}