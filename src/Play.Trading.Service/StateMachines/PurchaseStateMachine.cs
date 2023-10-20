using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Play.Common.Settings;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Contracts;
using Play.Trading.Service.Activities;
using Play.Trading.Service.SignalR;

namespace Play.Trading.Service.StateMachines;


public class PurchaseStateMachine : MassTransitStateMachine<PurchaseState>
{
    /*
    * For Premetheus: The first thing that we want to think about is what exactly is that we want to start tracking as metrics.
    * So in the case of our trading microservice, what I think we can start tracking is basically three things. We want to track
    * any time a new purchase has started. We also want to track anytime a purchase has succeeded. So we went through all the 
    * state machine and the purchase complete successfully. And we also want to track any time the purchase fails for any reason.
    * Either we cannot calculate the purchase total or any of our collaborating microservices are not able to perform their task.
    * So those are three things that we are going to keep track of. So a purchase that starts, a purchase that succeeds, and a
    * purchase that fails.
    */
    private readonly Counter<int> _purchaseStartedCounter;
    private readonly Counter<int> _purchaseSuccessCounter;
    private readonly Counter<int> _purchaseFailedCounter;


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

    public PurchaseStateMachine(MessageHub hub, ILogger<PurchaseStateMachine> logger, IConfiguration configuration)
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

        /*
        * Premetheus: we're going to be needing the service name of our microservice to define what we call a Meter that will also
        * lat us create the counters. The Meter is the entry point for all the metrics tracking of your microservice. So usually 
        * you'll have at least one Meter that owns everything related to metrics in your microservice.
        */
        var settings = configuration.GetSection(nameof(ServiceSettings)).Get<ServiceSettings>();
        Meter meter = new(settings.ServiceName);
        _purchaseStartedCounter = meter.CreateCounter<int>("PurchaseStarted");
        _purchaseSuccessCounter = meter.CreateCounter<int>("PurchaseSuccess");
        _purchaseFailedCounter = meter.CreateCounter<int>("PurchaseFailed");
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

                    /*
                    * Premetheus:
                    * .Add(1 ..., "1" is how many you want to count. Since this is just a brand new purchase, we are going to be counting one. That's the Delta
                    * That you're adding with this counter. And that's really all you have to do start counting. But the one additional thing that you can 
                    * do here is to add what we call dimensions. So dimensions are additional metadata associated to this measurement that you're performing 
                    * here. So in this case, we're saying that we have a brand new purchase has started, but what we can also say about this of what item.
                    * So it's a purchase of what. And so we can attach a dimension that will tell us that this is a purchase of let's say a potion or an either 
                    * or antidote or whatever it is. So to do that, what we can do is just add "new KeyValuePair" here. And that's how the counter expects it
                    * to receive it. So what is going to be the key for this counter? The key is going to be just the name of the property. That is the key
                    * of the dimension. For the next parameter, the object, the actual value of the dimension, we are going to be using the ItemId. So with
                    * this we have added a counter that so anytime a purchase starts, it's going to count one. So one more purchase and it is going to 
                    * keep track of a dimension that's going to be called item id and whose value is going to be the Id of the Item that is being tracked.
                    * So sadly, it is not a nice as showing the actual name of the item, but will at least give us the Id of the Item that is being tracked here
                    * 
                    */
                    _purchaseStartedCounter.Add(1, new KeyValuePair<string, object>(nameof(context.Saga.ItemId), context.Saga.ItemId)); // boxing ItemId to object
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

                    /*
                    * Premetheus: count the operation failed
                    */
                    _purchaseFailedCounter.Add(1, new KeyValuePair<string, object>(nameof(context.Saga.ItemId), context.Saga.ItemId)); // boxing ItemId to object
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

                    /*
                    * Premetheus: count the operation failed
                    */
                    _purchaseFailedCounter.Add(1, new KeyValuePair<string, object>(nameof(context.Saga.ItemId), context.Saga.ItemId)); // boxing ItemId to object
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

                    /*
                    * Premetheus: count the operation failed
                    */
                    _purchaseSuccessCounter.Add(1, new KeyValuePair<string, object>(nameof(context.Saga.ItemId), context.Saga.ItemId)); // boxing ItemId to object
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

                    /*
                    * Premetheus: count the operation failed
                    */
                    _purchaseFailedCounter.Add(1, new KeyValuePair<string, object>(nameof(context.Saga.ItemId), context.Saga.ItemId)); // boxing ItemId to object
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