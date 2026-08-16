using Domain.Entities;
using Infrastructure.Callbacks;

namespace Infrastructure.Messaging;

internal interface IMoneyEventDispatcher
{
    Task<CallbackDeliveryResult> DispatchAsync(
        OutboundMoneyEvent moneyEvent,
        CancellationToken cancellationToken = default);
}
