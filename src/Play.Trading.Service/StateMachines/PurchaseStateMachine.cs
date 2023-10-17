using System;
using Automatonymous;
using MassTransit;
using Microsoft.Extensions.Logging;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Contracts;
using Play.Trading.Service.Activities;
using Play.Trading.Service.SignalR;

namespace Play.Trading.Service.StateMachines;

public class PurchaseStateMachine : MassTransitStateMachine<PurchaseState>
{
    private readonly MessageHub _hub;
    private readonly ILogger<PurchaseStateMachine> _logger;
    public State Accepted { get; }
    public State ItemsGranted { get; }
    public State Completed { get; }
    public State Faulted { get; }

    public Event<PurchaseRequested> PurchaseRequested { get; }

    public Event<GetPurchaseState> GetPurchaseState { get; }

    public Event<InventoryItemsGranted> InventoryItemsGranted { get; }

    public Event<GilDebited> GilDebited { get; }

    public Event<Fault<GrantItems>> GrantItemsFaulted { get; }

    public Event<Fault<DebitGil>> DebitGilFaulted { get; }

    public PurchaseStateMachine(MessageHub hub, ILogger<PurchaseStateMachine> logger)
    {
        /*
        * The way we can tell MassTransit which is going to be the current state is by using the InstanceState 
        * method
        * That's how we find where we are going to keep tracking the state
        */
        InstanceState(state => state.CurrentState);
        ConfigureEvents();
        ConfigureInitialState();
        ConfigureAny();
        ConfigureAccepted();
        ConfigureItemsGranted();
        ConfigureFaulted();
        ConfigureCompleted();
        _hub = hub;
        _logger = logger;
    }

    private void ConfigureEvents()
    {
        //Saga flow
        Event(() => PurchaseRequested);
        Event(() => InventoryItemsGranted);
        Event(() => GilDebited);
        Event(() => GrantItemsFaulted, x => x.CorrelateById(context => context.Message.Message.CorrelationId));
        Event(() => DebitGilFaulted, x => x.CorrelateById(context => context.Message.Message.CorrelationId));


        //Get
        Event(() => GetPurchaseState);
    }

    private void ConfigureInitialState()
    {

        Initially(
            /*
            * We have to say what should happen as the state machine gets activated, as your new instance gets created
            */
            When(PurchaseRequested)
                /*
                * This means that whenever we receive the PurchaseRequested event, then we should go ahead and capture 
                * all these properties.
                */
                .Then(context =>
                {
                    context.Instance.UserId = context.Data.UserId;
                    context.Instance.ItemId = context.Data.ItemId;
                    context.Instance.Quantity = context.Data.Quantity;
                    context.Instance.Received = DateTimeOffset.UtcNow;
                    context.Instance.LastUpdated = context.Instance.Received;

                    _logger.LogInformation("Calculating total price for purchase with CorrelationId {CorrelationId}..."
                    , context.Instance.CorrelationId);
                })
                //Do calculation
                .Activity(x => x.OfType<CalculatePurchaseTotalActivity>())
                //send to Inventory, map this GrantItems to queue inventory-grant-items at StartUp class
                .Send(context => new GrantItems(
                    context.Instance.UserId,
                    context.Instance.ItemId,
                    context.Instance.Quantity,
                    context.Instance.CorrelationId))
                /*
                * After that we need to set what's going to be the current state of the state machine.
                */
                .TransitionTo(Accepted)
                //if throw an exception (e.g UnknownItemException, we will do sth as below)
                .Catch<Exception>(ex => ex.Then(context =>
                {
                    context.Instance.ErrorMessage = context.Exception.Message;
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogError(context.Exception, "Could not calculate the total price of purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}"
                    , context.Instance.CorrelationId
                    , context.Instance.ErrorMessage);
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Instance)))
        );
    }

    private void ConfigureAccepted()
    {
        During
        (
            Accepted,
            Ignore(PurchaseRequested),
            When(InventoryItemsGranted)
                .Then(context =>
                {
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogInformation("Items of purchase with CorrelationId {CorrelationId} have been granted to user {UserId}."
                    , context.Instance.CorrelationId
                    , context.Instance.UserId);
                })
                .Send(context => new DebitGil(
                    context.Instance.UserId,
                    context.Instance.PurchaseTotal.Value,
                    context.Instance.CorrelationId
                ))
                .TransitionTo(ItemsGranted),
            When(GrantItemsFaulted)
                .Then(context =>
                {
                    context.Instance.ErrorMessage = context.Data.Exceptions[0].Message;
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogError("Could not grant items for purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}."
                    , context.Instance.CorrelationId
                    , context.Instance.ErrorMessage);
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Instance))
        );
    }

    private void ConfigureItemsGranted()
    {
        During
        (
            ItemsGranted,
            Ignore(PurchaseRequested),
            Ignore(InventoryItemsGranted),
            When(GilDebited)
                .Then(context =>
                {
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogInformation("The total price of purchase with CorrelationId {CorrelationId} has been debited from user {UserId}. Purchase complete."
                    , context.Instance.CorrelationId
                    , context.Instance.UserId);
                })
                .TransitionTo(Completed)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Instance)),
            When(DebitGilFaulted)
                .Send(context => new SubtractItems(
                    context.Instance.UserId,
                    context.Instance.ItemId,
                    context.Instance.Quantity,
                    context.Instance.CorrelationId
                ))
                .Then(context =>
                {
                    context.Instance.ErrorMessage = context.Data.Exceptions[0].Message;
                    context.Instance.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogError("Could not debit the total price of purchase with CorrelationId {CorrelationId} from User {UserId}. Error: {ErrorMessage}."
                    , context.Instance.CorrelationId
                    , context.Instance.UserId
                    , context.Instance.ErrorMessage);
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Instance))
        );
    }

    private void ConfigureCompleted()
    {
        During(Completed,
            Ignore(PurchaseRequested),
            Ignore(InventoryItemsGranted),
            Ignore(GilDebited)
        );
    }

    private void ConfigureAny()
    {
        DuringAny(
            When(GetPurchaseState)
                .Respond(x => x.Instance)
        );
    }

    private void ConfigureFaulted()
    {
        //if we have another message with the same correlationId when the state is faulted, we ignore InventoryItemsGranted, PurchaseRequested, and GilDebited
        During(Faulted,
            Ignore(PurchaseRequested),
            Ignore(InventoryItemsGranted),
            Ignore(GilDebited)
            );
    }
}