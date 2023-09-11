using System;
using System.Security.Claims;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Play.Trading.Contracts;
using Play.Trading.Service.Dtos;
using Play.Trading.Service.StateMachines;

namespace Play.Trading.Service.Controllers;

[ApiController]
[Route("purchase")]
[Authorize]
public class PurchaseController : ControllerBase
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IRequestClient<GetPurchaseState> _purchaseClient;

    public PurchaseController(IPublishEndpoint publishEndpoint, IRequestClient<GetPurchaseState> purchaseClient)
    {
        _publishEndpoint = publishEndpoint;
        _purchaseClient = purchaseClient;
    }

    [HttpGet("status/{idempotencyId}")]
    public async Task<ActionResult<PurchaseDto>> GetStatusAsync(Guid idempotencyId)
    {
        /*
        * This looks like an asynchronous communication against a state machine, but it is in fact still
        * still asynchronous communication. It is just that this creates temporary queue behind the scences 
        * and the client here is actually waiting for a response from the other side. So image that a message
        * goes into the queue of the state machine and then the client is just waiting on his own queue for
        * another message to come back and respond.
        */
        var response = await _purchaseClient.GetResponse<PurchaseState>(new GetPurchaseState(idempotencyId));

        var purchaseState = response.Message;

        var purchase = new PurchaseDto(
            purchaseState.UserId,
            purchaseState.ItemId,
            purchaseState.PurchaseTotal,
            purchaseState.Quantity,
            purchaseState.CurrentState,
            purchaseState.ErrorMessage,
            purchaseState.Received,
            purchaseState.LastUpdated
        );

        return Ok(purchase);
    }

    [HttpPost]
    public async Task<IActionResult> PostAsync(SubmitPurchaseDto purchase)
    {
        var userId = User.FindFirstValue("sub");

        var message = new PurchaseRequested(Guid.Parse(userId), purchase.ItemId.Value, purchase.Quantity, purchase.IdempotencyId.Value);

        await _publishEndpoint.Publish(message);

        return AcceptedAtAction(nameof(GetStatusAsync), new { purchase.IdempotencyId }, new { purchase.IdempotencyId });
    }
}