using Application.Common;
using Application.Interfaces;
using Domain.Exceptions;
using Domain.ValueObjects;
using MediatR;

namespace Application.Commands.UpdateProduct;

internal sealed class UpdateProductCommandHandler(IProductPersistenceService persistence)
    : IRequestHandler<UpdateProductCommand, Result>
{
    public async Task<Result> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var productId = ProductId.From(request.ProductId);
            var product   = await persistence.GetByIdAsync(productId, cancellationToken);
            if (product is null)
                return Result.Failure($"Product with ID {request.ProductId} was not found.");

            var categoryId = CategoryId.From(request.CategoryId);

            if (product.Status == ProductStatus.Approved
                && HasIdentityChanges(product, request, categoryId))
            {
                return Result.Failure("Identity updates require moderation and are temporarily disabled, available updates only for price and stock");
            }

            var price      = Money.Create(request.Price, request.Currency);
            var attributes = request.Attributes
                .Select(a => new Domain.ValueObjects.ProductAttribute(a.Key, a.Value)).ToList();

            product.Update(request.Name, request.Description, categoryId, price, attributes, request.ImageUrls);
            await persistence.UpdateProductAsync(product, cancellationToken);

            return Result.Success();
        }
        catch (DomainException ex) { return Result.Failure(ex.Message); }
    }

    private static bool HasIdentityChanges(Domain.Entities.Product product, UpdateProductCommand request, CategoryId categoryId)
    {
        if (!string.Equals(product.Name, request.Name, StringComparison.Ordinal))
            return true;

        if (!string.Equals(product.Description, request.Description, StringComparison.Ordinal))
            return true;

        if (!product.CategoryId.Equals(categoryId))
            return true;

        var requestedImageUrls = request.ImageUrls ?? [];
        return !product.ImageUrls.SequenceEqual(requestedImageUrls);
    }
}
