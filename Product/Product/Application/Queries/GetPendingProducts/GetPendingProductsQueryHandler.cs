using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using MediatR;

namespace Application.Queries.GetPendingProducts;

internal sealed class GetPendingProductsQueryHandler(IProductReadRepository repository)
    : IRequestHandler<GetPendingProductsQuery, Result<PagedResult<ProductDetailDto>>>
{
    public async Task<Result<PagedResult<ProductDetailDto>>> Handle(
        GetPendingProductsQuery request, CancellationToken cancellationToken)
    {
        var result = await repository.GetPendingReviewAsync(request.Page, request.Size, cancellationToken);
        return Result<PagedResult<ProductDetailDto>>.Success(result);
    }
}
