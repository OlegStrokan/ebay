using Application.Common;
using Application.Interfaces;
using Domain.Exceptions;
using Domain.ValueObjects;
using MediatR;

namespace Application.Commands.ApproveProduct;

internal sealed class ApproveProductCommandHandler(IProductPersistenceService persistence)
    : IRequestHandler<ApproveProductCommand, Result>
{
    public async Task<Result> Handle(ApproveProductCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var product = await persistence.GetByIdAsync(ProductId.From(request.ProductId), cancellationToken);
            if (product is null)
                return Result.Failure($"Product with ID {request.ProductId} was not found.");

            product.Approve();
            await persistence.UpdateProductAsync(product, cancellationToken);
            return Result.Success();
        }
        catch (DomainException ex) { return Result.Failure(ex.Message); }
    }
}
