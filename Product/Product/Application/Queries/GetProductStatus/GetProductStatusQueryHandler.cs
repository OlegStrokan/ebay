using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Domain.ValueObjects;
using MediatR;

namespace Application.Queries.GetProductStatus;

internal sealed class GetProductStatusQueryHandler(IProductPersistenceService persistence)
    : IRequestHandler<GetProductStatusQuery, Result<ProductStatusDto>>
{
    public async Task<Result<ProductStatusDto>> Handle(
        GetProductStatusQuery request, CancellationToken cancellationToken)
    {
        var product = await persistence.GetByIdAsync(ProductId.From(request.ProductId), cancellationToken);
        if (product is null)
            return Result<ProductStatusDto>.Failure($"Product with ID {request.ProductId} was not found.");

        if (product.SellerId != SellerId.From(request.SellerId))
            return Result<ProductStatusDto>.Failure("You do not have permission to view this product's status.");

        return Result<ProductStatusDto>.Success(
            new ProductStatusDto(product.Id.Value, product.Status.Name, product.ReviewNotes));
    }
}
