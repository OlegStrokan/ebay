using Api.Mappers;
using Application.Commands.CancelB2BOrder;
using Application.Commands.FinalizeQuote;
using Application.Commands.StartB2BOrder;
using Application.Commands.UpdateQuoteDraft;
using Application.DTOs;
using Application.Queries;
using FluentValidation;
using Grpc.Core;
using MediatR;
using Protos.Order;

namespace Api.GrpcServices;

public class B2BOrderGrpcService(
    IMediator mediator,
    ILogger<B2BOrderGrpcService> logger,
    IValidator<StartB2BOrderRequest> startValidator,
    IValidator<UpdateQuoteDraftRequest> updateValidator,
    IValidator<FinalizeQuoteRequest> finalizeValidator)
    : B2BOrderService.B2BOrderServiceBase
{
    public override async Task<B2BOrderResponse> StartB2BOrder(
        StartB2BOrderRequest request,
        ServerCallContext context)
    {
        try
        {
            var validation = await startValidator.ValidateAsync(request, context.CancellationToken);
            if (!validation.IsValid)
                return new B2BOrderResponse
                {
                    Success = false,
                    ErrorMessage = string.Join(", ", validation.Errors.Select(e => e.ErrorMessage))
                };

            var command = new StartB2BOrderCommand(
                CustomerId: Guid.Parse(request.CustomerId),
                CompanyName: request.CompanyName,
                DeliveryAddress: MapAddress(request.DeliveryAddress),
                IdempotencyKey: request.IdempotencyKey,
                CompanyId: request.HasCompanyId && Guid.TryParse(request.CompanyId, out var cid) ? cid : null);

            var result = await mediator.Send(command, context.CancellationToken);

            return result.IsSuccess
                ? new B2BOrderResponse { Success = true, B2BOrderId = result.Value.ToString() }
                : new B2BOrderResponse { Success = false, ErrorMessage = result.Error ?? "Unknown error" };
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            HandleException(ex, nameof(StartB2BOrder));
            throw;
        }
    }

    public override async Task<B2BOrderResponse> UpdateQuoteDraft(
        UpdateQuoteDraftRequest request,
        ServerCallContext context)
    {
        try
        {
            var validation = await updateValidator.ValidateAsync(request, context.CancellationToken);
            if (!validation.IsValid)
                return new B2BOrderResponse
                {
                    Success = false,
                    ErrorMessage = string.Join(", ", validation.Errors.Select(e => e.ErrorMessage))
                };

            var command = new UpdateQuoteDraftCommand(
                B2BOrderId: Guid.Parse(request.B2BOrderId),
                Changes: request.Changes.Select(MapChange).ToList(),
                Comment: string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment,
                CommentAuthor: string.IsNullOrWhiteSpace(request.CommentAuthor)
                    ? null
                    : request.CommentAuthor);

            var result = await mediator.Send(command, context.CancellationToken);

            return result.IsSuccess
                ? new B2BOrderResponse { Success = true, B2BOrderId = request.B2BOrderId }
                : new B2BOrderResponse { Success = false, ErrorMessage = result.Error ?? "Unknown error" };
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            HandleException(ex, nameof(UpdateQuoteDraft));
            throw;
        }
    }

    public override async Task<FinalizeQuoteResponse> FinalizeQuote(
        FinalizeQuoteRequest request,
        ServerCallContext context)
    {
        try
        {
            var validation = await finalizeValidator.ValidateAsync(request, context.CancellationToken);
            if (!validation.IsValid)
                return new FinalizeQuoteResponse { Success = false, ErrorMessage = string.Join(", ", validation.Errors.Select(e => e.ErrorMessage)) };

            var b2bOrderId = Guid.Parse(request.B2BOrderId);
            var command = new FinalizeQuoteCommand(b2bOrderId, request.PaymentMethod, request.IdempotencyKey);
            var result = await mediator.Send(command, context.CancellationToken);

            return result.IsSuccess
                ? new FinalizeQuoteResponse
                {
                    Success = true,
                    B2BOrderId = request.B2BOrderId,
                    OrderId = result.Value.ToString()
                }
                : new FinalizeQuoteResponse { Success = false, ErrorMessage = result.Error ?? "Unknown error" };
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            HandleException(ex, nameof(FinalizeQuote));
            throw;
        }
    }

    public override async Task<B2BOrderResponse> CancelB2BOrder(
        CancelB2BOrderRequest request,
        ServerCallContext context)
    {
        try
        {
            if (!Guid.TryParse(request.B2BOrderId, out var b2bOrderId))
                return new B2BOrderResponse { Success = false, ErrorMessage = "Invalid B2BOrderId format" };

            var command = new CancelB2BOrderCommand(
                b2bOrderId,
                request.Reasons.ToList());

            var result = await mediator.Send(command, context.CancellationToken);

            return result.IsSuccess
                ? new B2BOrderResponse { Success = true, B2BOrderId = request.B2BOrderId }
                : new B2BOrderResponse { Success = false, ErrorMessage = result.Error ?? "Unknown error" };
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            HandleException(ex, nameof(CancelB2BOrder));
            throw;
        }
    }

    public override async Task<GetB2BOrderResponse> GetB2BOrder(
        GetB2BOrderRequest request,
        ServerCallContext context)
    {
        try
        {
            if (!Guid.TryParse(request.B2BOrderId, out var b2bOrderId))
                return new GetB2BOrderResponse { Success = false, ErrorMessage = "Invalid B2BOrderId format" };

            var result = await mediator.Send(new GetB2BOrderQuery(b2bOrderId), context.CancellationToken);

            if (!result.IsSuccess)
                return new GetB2BOrderResponse { Success = false, ErrorMessage = result.Error };

            var s = result.Value!;
            var details = new B2BOrderDetails
            {
                Id = s.Id.ToString(),
                CustomerId = s.CustomerId.ToString(),
                CompanyName = s.CompanyName,
                Status = s.Status,
                TotalPrice = s.TotalPrice.ToDecimalValue(),
                Currency = s.Currency,
                DiscountPercent = s.DiscountPercent.ToDecimalValue(),
                DeliveryAddress = new Address
                {
                    Street = s.DeliveryAddress.Street,
                    City = s.DeliveryAddress.City,
                    Country = s.DeliveryAddress.Country,
                    PostalCode = s.DeliveryAddress.PostalCode
                },
                RequestedDeliveryDate = s.RequestedDeliveryDate?.ToString("O") ?? string.Empty,
                FinalizedOrderId = s.FinalizedOrderId?.ToString() ?? string.Empty,
                Version = s.Version
            };
            if (s.CompanyId.HasValue)
                details.CompanyId = s.CompanyId.Value.ToString();
            details.Comments.AddRange(s.Comments);
            details.Items.AddRange(s.Items.Select(i =>
            {
                var item = new B2BLineItemDetail
                {
                    LineItemId = i.LineItemId.ToString(),
                    ProductId = i.ProductId.ToString(),
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice.ToDecimalValue(),
                    Currency = i.Currency,
                    IsRemoved = i.IsRemoved
                };
                if (i.AdjustedUnitPrice.HasValue)
                    item.AdjustedUnitPrice = i.AdjustedUnitPrice.Value.ToDecimalValue();
                return item;
            }));

            return new GetB2BOrderResponse { Success = true, B2BOrder = details };
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            HandleException(ex, nameof(GetB2BOrder));
            throw;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void HandleException(Exception ex, string method)
    {
        if (ex is FormatException)
        {
            logger.LogWarning(ex, "Invalid GUID format in {Method}", method);
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid ID format."));
        }

        logger.LogError(ex, "Error during {Method}", method);
        throw new RpcException(new Status(StatusCode.Internal, $"Internal error in {method}"));
    }

    private static AddressDto MapAddress(Address a) =>
        new(a.Street, a.City, a.Country, a.PostalCode);

    private static QuoteItemChangeDto MapChange(QuoteItemChange c) => new(
        Type: Enum.Parse<QuoteChangeType>(c.ChangeType, ignoreCase: true),
        ProductId: Guid.TryParse(c.ProductId, out var pid) ? pid : null,
        Quantity: c.Quantity > 0 ? c.Quantity : null,
        Price: c.Price is not null ? c.Price.ToDecimal() : null,
        Currency: string.IsNullOrWhiteSpace(c.Currency) ? null : c.Currency);
}
