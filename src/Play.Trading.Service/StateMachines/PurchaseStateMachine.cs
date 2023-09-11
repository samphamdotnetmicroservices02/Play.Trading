using System;
using Automatonymous;
using MassTransit;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Contracts;
using Play.Trading.Service.Activities;
using Play.Trading.Service.SignalR;

namespace Play.Trading.Service.StateMachines;

public class PurchaseStateMachine : MassTransitStateMachine<PurchaseState>
{
    private readonly MessageHub _hub;
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

    public PurchaseStateMachine(MessageHub hub)
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