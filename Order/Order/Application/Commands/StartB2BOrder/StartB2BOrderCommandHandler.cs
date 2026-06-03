using Application.Common;
using Application.Gateways;
using Application.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.StartB2BOrder;

public class StartB2BOrderCommandHandler(
    IB2BOrderPersistenceService persistenceService,
    IIdempotencyRepository idempotencyRepository,
    IUserGateway userGateway,
    ILogger<StartB2BOrderCommandHandler> logger)
    : IRequestHandler<StartB2BOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(StartB2BOrderCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await idempotencyRepository.GetByKeyAsync(request.IdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                logger.LogInformation(
                    "Duplicate StartB2BOrder for key {Key}. Returning existing {Id}",
                    request.IdempotencyKey, existing.ResultId);
                return Result<Guid>.Success(existing.ResultId);
            }

            var customerProfile = await userGateway.GetUserProfileAsync(
                request.CustomerId,
                cancellationToken);

            if (!customerProfile.IsActive)
            {
                return Result<Guid>.Failure(
                    $"Customer {request.CustomerId} is blocked and cannot create orders");
            }

            var customerId = CustomerId.From(request.CustomerId);
            var address = Address.Create(
                request.DeliveryAddress.Street,
                request.DeliveryAddress.City,
                request.DeliveryAddress.Country,
                request.DeliveryAddress.PostalCode);

            var b2BOrder = Domain.Entities.B2BOrder.B2BOrder.Start(customerId, request.CompanyName, address, request.CompanyId);

            await persistenceService.StartB2BOrderAsync(b2BOrder, request.IdempotencyKey, cancellationToken);

            logger.LogInformation(
                "Started B2BOrder {B2BOrderId} for company '{Company}' (customer {CustomerId})",
                b2BOrder.Id.Value, request.CompanyName, request.CustomerId);

            return Result<Guid>.Success(b2BOrder.Id.Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start B2BOrder for customer {CustomerId}", request.CustomerId);
            return Result<Guid>.Failure(ex.Message);
        }
    }
}
