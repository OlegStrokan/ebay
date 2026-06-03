using Application.Common;
using Application.DTOs;

namespace Application.Commands.StartB2BOrder;

public record StartB2BOrderCommand(
    Guid CustomerId,
    string CompanyName,
    AddressDto DeliveryAddress,
    string IdempotencyKey,
    Guid? CompanyId = null) : ICommand<Result<Guid>>;
