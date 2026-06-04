using Application.Common;
using Application.DTOs;

namespace Application.Commands.CreateProduct;

public sealed record CreateProductCommand(
    Guid SellerId,
    string Name,
    string Description,
    decimal Price,
    string Currency,
    int InitialStock,
    List<ProductAttributeDto> Attributes,
    List<string> ImageUrls) : ICommand<Result<Guid>>;
