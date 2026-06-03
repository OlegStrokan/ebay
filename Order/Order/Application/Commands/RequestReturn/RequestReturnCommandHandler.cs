using System.Text.Json;
using Application.Common;
using Application.DTOs;
using Application.Gateways;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Entities.Order;
using Domain.Events.OrderReturn;
using Domain.Services;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.RequestReturn;

public class RequestReturnCommandHandler(
    IOrderPersistenceService orderPersistenceService,
    IIdempotencyRepository idempotencyRepository,
    IReturnRequestPersistenceService returnRequestPersistenceService,
    IUserGateway userGateway,
    ReturnPolicyService returnPolicyService,
    ILogger<RequestReturnCommandHandler> logger
        ) : IRequestHandler<RequestReturnCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        RequestReturnCommand request,
        CancellationToken cancellationToken)
    {
        try
        {

            var existingRecord = await idempotencyRepository.GetByKeyAsync(
                request.IdempotencyKey,
                cancellationToken);

            if (existingRecord != null)
            {
                logger.LogInformation(
                    "Duplicate return request detected for idempotency key {Key}. " +
                    "Returning existing order {OrderId}",
                    request.IdempotencyKey,
                    existingRecord.ResultId);

                return Result<Guid>.Success(existingRecord.ResultId);
            }
            
            logger.LogInformation(
                "Processing return request for order {OrderId}",
                request.OrderId);
            
                var order = await orderPersistenceService.LoadOrderAsync(
                    request.OrderId,
                    cancellationToken);

                if (order == null)
                {
                    return Result<Guid>.Failure($"Order {request.OrderId} not found");
                }
                
                if (order.Status != OrderStatus.Completed)
                    return Result<Guid>.Failure(
                        $"Order {request.OrderId} must be completed to request return. " +
                        $"Current status: {order.Status}");

                var existingReturn = await returnRequestPersistenceService.LoadByOrderIdAsync(
                    request.OrderId, cancellationToken);

                var itemsToReturn = new List<OrderItem>();
                foreach (var dto in request.ItemsToReturn)
                {
                    var originalItem = order.Items.FirstOrDefault(i => i.ProductId.Value == dto.ProductId);
                    if (originalItem == null)
                        return Result<Guid>.Failure(
                            $"Product {dto.ProductId} is not part of order {request.OrderId}");

                    var alreadyReturnedQty = existingReturn?.ItemsToReturn
                        .Where(i => i.ProductId.Value == dto.ProductId)
                        .Sum(i => i.Quantity) ?? 0;

                    var maxReturnableQty = originalItem.Quantity - alreadyReturnedQty;
                    if (dto.Quantity <= 0 || dto.Quantity > maxReturnableQty)
                        return Result<Guid>.Failure(
                            $"Cannot return {dto.Quantity} of product {dto.ProductId}. " +
                            $"Maximum returnable quantity is {maxReturnableQty}");

                    itemsToReturn.Add(OrderItem.Create(
                        originalItem.ProductId,
                        dto.Quantity,
                        originalItem.PriceAtPurchase));
                }

                var refundAmount = Order.CalculateTotalPrice(itemsToReturn);

                var customerProfile = await userGateway.GetUserProfileAsync(
                    order.CustomerId.Value,
                    cancellationToken);

                var policyContext = new ReturnPolicyContext(
                    CountryCode: customerProfile.CountryCode,
                    ProductCategories: new List<string>(), 
                    CustomerTier: customerProfile.CustomerTier,
                    IsHolidaySeason: ReturnPolicyService.IsHolidaySeason(DateTime.UtcNow) 
                );
                
                var returnWindow = returnPolicyService.CalculateReturnWindow(policyContext);

            
                var returnRequest = Domain.Entities.RequestReturn.RequestReturn.Create(
                    OrderId.From(request.OrderId),
                    order.CustomerId,
                    request.Reason,
                    itemsToReturn,
                    refundAmount,
                    order.CompletedAt!.Value, // already not null
                    order.Items.ToList(),
                    returnWindow);

                logger.LogInformation(
                    "ReturnRequest created with ID  {ReturnRequestId} for order {OrderId}",
                   returnRequest.Id.Value, order.Id.Value);

                var resultOrderId = await returnRequestPersistenceService.CreateReturnRequestAsync(
                    returnRequest,
                    request.IdempotencyKey,
                    returnRequest.Id.Value,
                    cancellationToken);
                
                logger.LogInformation(
                    "ReturnRequest saved successfully with {EventCount} events for order {OrderId}",
                    returnRequest.UncommitedEvents.Count,
                    resultOrderId);

                return Result<Guid>.Success(returnRequest.Id.Value);
        }

        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process for return request for order {OrderId}", request.OrderId);
            return Result<Guid>.Failure($"Failed to request return: {ex.Message}");
        }
    }
}