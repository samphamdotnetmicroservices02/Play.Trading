using System.Threading.Tasks;
using MassTransit;
using Play.Common;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Service.Entities;

namespace Play.Trading.Service.Consumers;

public class UserUpdatedConsumer : IConsumer<UserUpdated>
{
    private readonly IRepository<ApplicationUser> _repository;

    public UserUpdatedConsumer(IRepository<ApplicationUser> repository)
    {
        _repository = repository;
    }

    public async Task Consume(ConsumeContext<UserUpdated> context)
    {
        var message = context.Message;
        var user = await _repository.GetAsync(message.UserId);

        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = message.UserId,
                Gil = message.NewTotalGil
            };

            await _repository.CreateAsync(user);
        }
        else
        {
            user.Gil = message.NewTotalGil;

            await _repository.UpdateAsync(user);
        }
    }
}