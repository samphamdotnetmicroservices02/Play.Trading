using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Play.Trading.Service.StateMachines;

namespace Play.Trading.Service.SignalR;

//the main purpose of this class is to send message from server to the client

//Authorize is to make sure that the access token actively flows into our message hub, and via that, we can identify who is a user
// that's invoking this action
[Authorize]
public class MessageHub : Hub
{
    // The idea of this me this method is that our state machine will invoke it by passing in the entire purchase state,
    // and then we will forward that purchase state all the way back to the client using SignalR. So it's server to client communication
    public async Task SendStatusAsync(PurchaseState status)
    {
        /*
        * from teacher's thought, the best way to do this is to try to send a message directly to the client that is associated to
        * the locked-in user. And to do that, we can use the user method (Clients.User). So this method allows us to send a message
        * directly to the user that are dedicated into our microservice.
        * But before we can use this, we have to define sth else that is known as a userId provider (UserIdProvider.cs). So this is a class 
        * that can tell SignalR how to map one of the client connections into specific userId that has been locked in into the service.
        */
        /*
        * UserIdentifier is going to include that userId that we already mapped in ther UserIdProvider.cs, which is a sub claim
        * ReceivePurchaseStatus is which is the name of the method in the client, in this case, in the front end portal,
        * that we want to invoke from the server.
        */
        if (Clients is not null)
        {
            await Clients.User(Context.UserIdentifier)
                .SendAsync("ReceivePurchaseStatus", status);
        }
    }
}