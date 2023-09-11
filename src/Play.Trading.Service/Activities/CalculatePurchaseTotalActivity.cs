using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Automatonymous;
using GreenPipes;
using Play.Common;
using Play.Trading.Contracts;
using Play.Trading.Service.Entities;
using Play.Trading.Service.Exceptions;
using Play.Trading.Service.StateMachines;

namespace Play.Trading.Service.Activities;

public class CalculatePurchaseTotalActivity : Activity<PurchaseState, PurchaseRequested>
{
    private readonly IRepository<CatalogItem> _repository;

    public CalculatePurchaseTotalActivity(IRepository<CatalogItem> repository)
    {
        _repository = repository;
    }

    public void Accept(StateMachineVisitor visitor)
    {
        visitor.Visit(this);
    }

    public async Task Execute(BehaviorContext<PurchaseState, PurchaseRequested> context, Behavior<PurchaseState, PurchaseRequested> next)
    {
        /*
        * message arrives before invoking activity
        */
        var message = context.Data;
        var item = await _repository.GetAsync(message.ItemId);

        if (item is null)
        {
            throw new UnknownItemException(message.ItemId);
        }

        context.Instance.PurchaseTotal = item.Price * message.Quantity;
        context.Instance.LastUpdated = DateTimeOffset.UtcNow;

        await next.Execute(context).ConfigureAwait(false);

    }

    public Task Faulted<TException>(BehaviorExceptionContext<PurchaseState, PurchaseRequested, TException> context, Behavior<PurchaseState, PurchaseRequested> next) where TException : Exception
    {
        return next.Faulted(context);
    }

    public void Probe(ProbeContext context)
    {
        /*
        * this method is really to provide some information that we could potentially use to visualized the state
        * of the activity if you wanted to
        */
        context.CreateScope("calculate-purchase-total");
    }
}