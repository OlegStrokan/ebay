using Application.Common;
using Application.DTOs;
using Application.Gateways;
using Application.Gateways.Models;
using Application.Interfaces;
using Application.Mappers;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Commands.ProcessPayment;

// handle request from order service
internal sealed class ProcessPaymentCommandHandler(
    IPaymentRepository paymentRepository,
    IStripePaymentProvider stripePaymentProvider,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ProcessPaymentCommandHandler> logger)
    : IRequestHandler<ProcessPaymentCommand, Result<ProcessPaymentResultDto>>
{
    public async Task<Result<ProcessPaymentResultDto>> Handle(
        ProcessPaymentCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var idempotencyKey = IdempotencyKey.From(request.IdempotencyKey);
            var existing = await paymentRepository.GetByOrderIdAndIdempotencyKeyAsync(
                request.OrderId,
                idempotencyKey,
                cancellationToken);

            if (existing is not null)
            {
                logger.LogInformation(
                    "Returning idempotent payment response for order {OrderId} and key {IdempotencyKey}",
                    request.OrderId,
                    request.IdempotencyKey);

                return Result<ProcessPaymentResultDto>.Success(
                    PaymentDtoMapper.ToProcessPaymentResult(existing));
            }

            var amount = Money.Create(request.Amount, request.Currency);

            var payment = Payment.Create(
                PaymentId.CreateUnique(),
                request.OrderId,
                request.CustomerId,
                amount,
                request.PaymentMethod,
                idempotencyKey,
                clock.UtcNow);

            var providerResult = await stripePaymentProvider.ProcessPaymentAsync(
                new ProcessPaymentProviderRequest(
                    PaymentId: payment.Id.Value,
                    OrderId: payment.OrderId,
                    CustomerId: payment.CustomerId,
                    Amount: payment.Amount.Amount,
                    Currency: payment.Amount.Currency,
                    PaymentMethod: payment.Method,
                    IdempotencyKey: idempotencyKey.Value,
                    CustomerEmail: request.CustomerEmail),
                cancellationToken);

            var now = clock.UtcNow;

            ProcessPaymentStatus responseStatus;

            switch (providerResult.Status)
            {
                case ProviderProcessPaymentStatus.Succeeded:
                {
                    var providerIntentId = string.IsNullOrWhiteSpace(providerResult.ProviderPaymentIntentId)
                        ? null
                        : ProviderPaymentIntentId.From(providerResult.ProviderPaymentIntentId);

                    payment.MarkSucceeded(providerIntentId, now);
                    responseStatus = ProcessPaymentStatus.Succeeded;
                    break;
                }
                case ProviderProcessPaymentStatus.Pending:
                case ProviderProcessPaymentStatus.RequiresAction:
                {
                    if (string.IsNullOrWhiteSpace(providerResult.ProviderPaymentIntentId))
                    {
                        return Result<ProcessPaymentResultDto>.Failure(
                            "ProviderPaymentIntentId is required when provider status is Pending or RequiresAction");
                    }

                    payment.MarkPendingProviderConfirmation(
                        ProviderPaymentIntentId.From(providerResult.ProviderPaymentIntentId),
                        now);

                    responseStatus = providerResult.Status == ProviderProcessPaymentStatus.RequiresAction
                        ? ProcessPaymentStatus.RequiresAction
                        : ProcessPaymentStatus.Pending;
                    break;
                }
                case ProviderProcessPaymentStatus.Failed:
                {
                    var reason = FailureReason.Create(
                        providerResult.ErrorCode,
                        providerResult.ErrorMessage ?? "Payment provider returned failure status.");

                    payment.MarkFailed(reason, now);
                    responseStatus = ProcessPaymentStatus.Failed;
                    break;
                }
                default:
                    return Result<ProcessPaymentResultDto>.Failure("Unsupported provider payment status.");
            }

            await paymentRepository.AddAsync(payment, cancellationToken);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException ex)
            {
                logger.LogInformation(
                    ex,
                    "Detected concurrent idempotent payment create for order {OrderId} and key {IdempotencyKey}. Returning persisted payment.",
                    request.OrderId,
                    request.IdempotencyKey);

                var persistedPayment = await paymentRepository.GetByOrderIdAndIdempotencyKeyAsync(
                    request.OrderId,
                    idempotencyKey,
                    cancellationToken);

                if (persistedPayment is not null)
                {
                    return Result<ProcessPaymentResultDto>.Success(
                        PaymentDtoMapper.ToProcessPaymentResult(persistedPayment));
                }

                logger.LogWarning(
                    ex,
                    "Unique constraint violation occurred for order {OrderId}, key {IdempotencyKey}, but no persisted idempotent payment was found after conflict.",
                    request.OrderId,
                    request.IdempotencyKey);

                return Result<ProcessPaymentResultDto>.Failure(
                    "Concurrent idempotent payment conflict. Please retry the request.");
            }

            return Result<ProcessPaymentResultDto>.Success(
                PaymentDtoMapper.ToProcessPaymentResult(
                    payment,
                    overrideStatus: responseStatus,
                    clientSecret: providerResult.ClientSecret,
                    errorCode: providerResult.ErrorCode,
                    errorMessage: providerResult.ErrorMessage));
        }
        catch (DomainException ex)
        {
            logger.LogWarning(ex, "ProcessPayment domain validation failed");
            return Result<ProcessPaymentResultDto>.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ProcessPayment failed unexpectedly");
            return Result<ProcessPaymentResultDto>.Failure("Unexpected error while processing payment.");
        }
    }
}