using System;
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
                    context.Saga.UserId = context.Message.UserId;
                    context.Saga.ItemId = context.Message.ItemId;
                    context.Saga.Quantity = context.Message.Quantity;
                    context.Saga.Received = DateTimeOffset.UtcNow;
                    context.Saga.LastUpdated = context.Saga.Received;

                    _logger.LogInformation("Calculating total price for purchase with CorrelationId {CorrelationId}..."
                    , context.Saga.CorrelationId);
                })
                //Do calculation
                .Activity(x => x.OfType<CalculatePurchaseTotalActivity>())
                //send to Inventory, map this GrantItems to queue inventory-grant-items at StartUp class
                .Send(context => new GrantItems(
                    context.Saga.UserId,
                    context.Saga.ItemId,
                    context.Saga.Quantity,
                    context.Saga.CorrelationId))
                /*
                * After that we need to set what's going to be the current state of the state machine.
                */
                .TransitionTo(Accepted)
                //if throw an exception (e.g UnknownItemException, we will do sth as below)
                .Catch<Exception>(ex => ex.Then(context =>
                {
                    context.Saga.ErrorMessage = context.Exception.Message;
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogError(context.Exception, "Could not calculate the total price of purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}"
                    , context.Saga.CorrelationId
                    , context.Saga.ErrorMessage);
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Saga)))
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
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogInformation("Items of purchase with CorrelationId {CorrelationId} have been granted to user {UserId}."
                    , context.Saga.CorrelationId
                    , context.Saga.UserId);
                })
                .Send(context => new DebitGil(
                    context.Saga.UserId,
                    context.Saga.PurchaseTotal.Value,
                    context.Saga.CorrelationId
                ))
                .TransitionTo(ItemsGranted),
            When(GrantItemsFaulted)
                .Then(context =>
                {
                    context.Saga.ErrorMessage = context.Message.Exceptions[0].Message;
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogError("Could not grant items for purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}."
                    , context.Saga.CorrelationId
                    , context.Saga.ErrorMessage);
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Saga))
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
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogInformation("The total price of purchase with CorrelationId {CorrelationId} has been debited from user {UserId}. Purchase complete."
                    , context.Saga.CorrelationId
                    , context.Saga.UserId);
                })
                .TransitionTo(Completed)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Saga)),
            When(DebitGilFaulted)
                .Send(context => new SubtractItems(
                    context.Saga.UserId,
                    context.Saga.ItemId,
                    context.Saga.Quantity,
                    context.Saga.CorrelationId
                ))
                .Then(context =>
                {
                    context.Saga.ErrorMessage = context.Message.Exceptions[0].Message;
                    context.Saga.LastUpdated = DateTimeOffset.UtcNow;

                    _logger.LogError("Could not debit the total price of purchase with CorrelationId {CorrelationId} from User {UserId}. Error: {ErrorMessage}."
                    , context.Saga.CorrelationId
                    , context.Saga.UserId
                    , context.Saga.ErrorMessage);
                })
                .TransitionTo(Faulted)
                .ThenAsync(async context => await _hub.SendStatusAsync(context.Saga))
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
                .Respond(x => x.Saga)
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