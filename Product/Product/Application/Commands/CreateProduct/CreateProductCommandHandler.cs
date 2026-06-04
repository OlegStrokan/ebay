using Application.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.ValueObjects;
using MediatR;

namespace Application.Commands.CreateProduct;

internal sealed class CreateProductCommandHandler(IProductPersistenceService persistence)
    : IRequestHandler<CreateProductCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var sellerId   = SellerId.From(request.SellerId);
            var price      = Money.Create(request.Price, request.Currency);
            var attributes = request.Attributes.Select(a => new ProductAttribute(a.Key, a.Value)).ToList();

            var product = Product.Create(
                sellerId, request.Name, request.Description, CategoryId.Placeholder,
                price, request.InitialStock, attributes, request.ImageUrls);

            await persistence.CreateProductAsync(product, cancellationToken);

            return Result<Guid>.Success(product.Id.Value);
        }
        catch (DomainException ex)
        {
            return Result<Guid>.Failure(ex.Message);
        }
        // Infrastructure exceptions (DB failures, serialization errors, etc.) propagate
        // so the caller's exception handler can log them with full stack trace.
    }
}
