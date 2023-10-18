/*
* The purpose of this class is to represent the current state of the state machine. And here we're going to put
* a bunch of properties that we're going to filling as the state machine moves forward.
*/

using System;
using MassTransit;

namespace Play.Trading.Service.StateMachines;

/*
* we need to implement this interface (SagaStateMachineInstance) because each of these sagas are going to actually
* turn into instances as we drive each of our purchase processes. And so anytime we start a purchase process,
* a new instance of the purchase saga is going to be created.
*/
public class PurchaseState : SagaStateMachineInstance, ISagaVersion
{
    /*
    * This CorrelationId is what uniquely identifies each instance of our state machine.
    */
    public Guid CorrelationId { get; set; }

    /*
    * And so the current state is as it says, the state in which we are in the state machine. So if we are accepted
    * or if we granted items to the user, or we are completed, we'll define those states later on in the state 
    * machine. But this is where we'll start them.
    */
    public string CurrentState {get; set; }

    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }

    public int Quantity { get; set; }
    public DateTimeOffset Received { get; set; }

    /*
    * The PurchaseTotal is the property where we're going to store the calculation of the total amount of items,
    * so the quantity amount multiplied by the price of the item that is going to be purchased. That's so that 
    * we later on know how much to debit from the user's wallet.
    */
    public decimal? PurchaseTotal { get; set; }

    public DateTimeOffset LastUpdated { get; set; }

    /*
    * This going to become handy, so that we can store any error message that happened as we were processing 
    * the state machine. So that then we know what actually happened.
    */
    public string ErrorMessage { get; set; }

    /*
    * basically, this Version field or property (coming from ISagaVersion) is used for optimistic concurrency
    * as we store data in the state machine only for MongoDb
    */
    public int Version { get; set; }
}