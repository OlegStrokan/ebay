using Application.Common;
using Application.Interfaces;
using Domain.Exceptions;
using Domain.ValueObjects;
using MediatR;

namespace Application.Commands.RejectProduct;

internal sealed class RejectProductCommandHandler(IProductPersistenceService persistence)
    : IRequestHandler<RejectProductCommand, Result>
{
    public async Task<Result> Handle(RejectProductCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var product = await persistence.GetByIdAsync(ProductId.From(request.ProductId), cancellationToken);
            if (product is null)
                return Result.Failure($"Product with ID {request.ProductId} was not found.");

            product.Reject(request.Reason);
            await persistence.UpdateProductAsync(product, cancellationToken);
            return Result.Success();
        }
        catch (DomainException ex) { return Result.Failure(ex.Message); }
    }
}
