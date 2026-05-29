using Application.Common;
using Application.DTOs;
using MediatR;

namespace Application.Queries.GetPendingProducts;

public sealed record GetPendingProductsQuery(int Page, int Size) : IRequest<Result<PagedResult<ProductDetailDto>>>;
