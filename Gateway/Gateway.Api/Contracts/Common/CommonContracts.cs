namespace Gateway.Api.Contracts.Common;

public sealed record ApiResponse<T>(T Data);

public sealed record AddressDto(string Street, string City, string Country, string PostalCode);

public sealed record OrderItemDto(string ProductId, int Quantity, decimal Price, string Currency);
