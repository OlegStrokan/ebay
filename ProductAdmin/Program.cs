using ProductAdmin.Auth;
using ProductAdmin.Endpoints;
using Protos.Product;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpcClient<ProductService.ProductServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration["ProductServiceUrl"]
                             ?? "http://localhost:5050");
});

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapProductModerationEndpoints();
app.MapProductAdminEndpoints();
app.MapCatalogItemEndpoints();

app.Run();
