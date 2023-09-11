using System;

namespace Play.Trading.Contracts;

public record PurchaseRequested(Guid UserId, Guid ItemId, int Quantity, Guid CorrelationId);

public record GetPurchaseState(Guid CorrelationId);