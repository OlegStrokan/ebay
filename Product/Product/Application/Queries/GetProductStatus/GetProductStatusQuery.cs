using Application.Common;
using Application.DTOs;
using MediatR;

namespace Application.Queries.GetProductStatus;

public sealed record GetProductStatusQuery(Guid ProductId, Guid SellerId) : IRequest<Result<ProductStatusDto>>;
