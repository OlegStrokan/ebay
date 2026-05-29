using Grpc.Core;
using Protos.Product;

namespace Api.GrpcServices;

public sealed class ProductGrpcService(
    ProductGrpcHandler productHandler,
    ListingGrpcHandler listingHandler)
    : ProductService.ProductServiceBase
{
    public override Task<GetProductPricesResponse> GetProductPrices(
        GetProductPricesRequest request, ServerCallContext context)
        => productHandler.GetProductPrices(request, context.CancellationToken);

    public override Task<GetProductsResponse> GetProducts(
        GetProductsRequest request, ServerCallContext context)
        => productHandler.GetProducts(request, context.CancellationToken);

    public override Task<GetProductResponse> GetProduct(
        GetProductRequest request, ServerCallContext context)
        => productHandler.GetProduct(request, context.CancellationToken);

    public override Task<CreateProductResponse> CreateProduct(
        CreateProductRequest request, ServerCallContext context)
        => productHandler.CreateProduct(request, context.CancellationToken);

    public override Task<UpdateProductResponse> UpdateProduct(
        UpdateProductRequest request, ServerCallContext context)
        => productHandler.UpdateProduct(request, context.CancellationToken);

    public override Task<DeleteProductResponse> DeleteProduct(
        DeleteProductRequest request, ServerCallContext context)
        => productHandler.DeleteProduct(request, context.CancellationToken);

    public override Task<ActivateProductResponse> ActivateProduct(
        ActivateProductRequest request, ServerCallContext context)
        => productHandler.ActivateProduct(request, context.CancellationToken);

    public override Task<DeactivateProductResponse> DeactivateProduct(
        DeactivateProductRequest request, ServerCallContext context)
        => productHandler.DeactivateProduct(request, context.CancellationToken);

    public override Task<UpdateProductStockResponse> UpdateProductStock(
        UpdateProductStockRequest request, ServerCallContext context)
        => productHandler.UpdateProductStock(request, context.CancellationToken);

    public override Task<ApproveProductResponse> ApproveProduct(
        ApproveProductRequest request, ServerCallContext context)
        => productHandler.ApproveProduct(request, context.CancellationToken);

    public override Task<RejectProductResponse> RejectProduct(
        RejectProductRequest request, ServerCallContext context)
        => productHandler.RejectProduct(request, context.CancellationToken);

    public override Task<GetPendingProductsResponse> GetPendingProducts(
        GetPendingProductsRequest request, ServerCallContext context)
        => productHandler.GetPendingProducts(request, context.CancellationToken);

    public override Task<GetProductStatusResponse> GetProductStatus(
        GetProductStatusRequest request, ServerCallContext context)
        => productHandler.GetProductStatus(request, context.CancellationToken);

    public override Task<GetListingPricesResponse> GetListingPrices(
        GetListingPricesRequest request, ServerCallContext context)
        => listingHandler.GetListingPrices(request, context.CancellationToken);

    public override Task<GetListingsResponse> GetListings(
        GetListingsRequest request, ServerCallContext context)
        => listingHandler.GetListings(request, context.CancellationToken);

    public override Task<GetListingResponse> GetListing(
        GetListingRequest request, ServerCallContext context)
        => listingHandler.GetListing(request, context.CancellationToken);

    public override Task<GetSellerListingsResponse> GetSellerListings(
        GetSellerListingsRequest request, ServerCallContext context)
        => listingHandler.GetSellerListings(request, context.CancellationToken);

    public override Task<GetListingsForCatalogItemResponse> GetListingsForCatalogItem(
        GetListingsForCatalogItemRequest request, ServerCallContext context)
        => listingHandler.GetListingsForCatalogItem(request, context.CancellationToken);

    public override Task<CreateCatalogItemResponse> CreateCatalogItem(
        CreateCatalogItemRequest request, ServerCallContext context)
        => listingHandler.CreateCatalogItem(request, context.CancellationToken);

    public override Task<UpdateCatalogItemResponse> UpdateCatalogItem(
        UpdateCatalogItemRequest request, ServerCallContext context)
        => listingHandler.UpdateCatalogItem(request, context.CancellationToken);

    public override Task<CreateCatalogItemWithListingResponse> CreateCatalogItemWithListing(
        CreateCatalogItemWithListingRequest request, ServerCallContext context)
        => listingHandler.CreateCatalogItemWithListing(request, context.CancellationToken);

    public override Task<UpdateCatalogItemAndListingResponse> UpdateCatalogItemAndListing(
        UpdateCatalogItemAndListingRequest request, ServerCallContext context)
        => listingHandler.UpdateCatalogItemAndListing(request, context.CancellationToken);

    public override Task<CreateListingResponse> CreateListing(
        CreateListingRequest request, ServerCallContext context)
        => listingHandler.CreateListing(request, context.CancellationToken);

    public override Task<ActivateListingResponse> ActivateListing(
        ActivateListingRequest request, ServerCallContext context)
        => listingHandler.ActivateListing(request, context.CancellationToken);

    public override Task<DeactivateListingResponse> DeactivateListing(
        DeactivateListingRequest request, ServerCallContext context)
        => listingHandler.DeactivateListing(request, context.CancellationToken);

    public override Task<DeleteListingResponse> DeleteListing(
        DeleteListingRequest request, ServerCallContext context)
        => listingHandler.DeleteListing(request, context.CancellationToken);

    public override Task<UpdateListingStockResponse> UpdateListingStock(
        UpdateListingStockRequest request, ServerCallContext context)
        => listingHandler.UpdateListingStock(request, context.CancellationToken);

    public override Task<ChangeListingPriceResponse> ChangeListingPrice(
        ChangeListingPriceRequest request, ServerCallContext context)
        => listingHandler.ChangeListingPrice(request, context.CancellationToken);
}
