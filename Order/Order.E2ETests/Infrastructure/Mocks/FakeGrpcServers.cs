using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Protos.Accounting;
using Protos.Common;
using Protos.Inventory;
using Protos.Payment;
using Protos.Product;

namespace Order.E2ETests.Infrastructure.Mocks;

public abstract class FakeGrpcServerBase : IAsyncDisposable
{
    private WebApplication? _app;
    public string Address { get; private set; } = string.Empty;

    protected abstract void RegisterServices(IServiceCollection services);
    protected abstract void MapServices(WebApplication app);

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
            
        });

        builder.Services.AddGrpc();
        RegisterServices(builder.Services);

        _app = builder.Build();
        MapServices(_app);
        await _app.StartAsync();

        var feature = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        Address = feature!.Addresses.First();
    }

    public async Task StopAsync()
    {
        if (_app is not null)
            await _app.StopAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}


//---------Payment Server---------//

public class FakePaymentGrpcServer : FakeGrpcServerBase
{
    public bool ProcessShouldSucceed { get; set; } = true;
    public StatusCode? ProcessRpcStatusCodeToThrow { get; set; }
    public string ProcessRpcErrorDetailToThrow { get; set; } = "simulated rpc failure";
    public ProcessPaymentStatus ProcessStatusToReturn { get; set; } = ProcessPaymentStatus.Succeeded;
    public string PaymentIdToReturn { get; set; } = "PAY-DEFAULT";
    public string ProviderPaymentIntentIdToReturn { get; set; } = "pi_test_default";
    public string ClientSecretToReturn { get; set; } = "cs_test_default";
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    public bool RefundShouldSucceed { get; set; } = true;
    public string RefundIdToReturn { get; set; } = "REF-DEFAULT";

    public List<ProcessPaymentRequest> ProcessCalls { get; } = new();
    public List<RefundPaymentRequest> RefundCalls { get; } = new();

    public void Reset()
    {
        ProcessShouldSucceed = true;
        ProcessRpcStatusCodeToThrow = null;
        ProcessRpcErrorDetailToThrow = "simulated rpc failure";
        ProcessStatusToReturn = ProcessPaymentStatus.Succeeded;
        PaymentIdToReturn = "PAY-" + Guid.NewGuid().ToString()[..8];
        ProviderPaymentIntentIdToReturn = "pi_test_" + Guid.NewGuid().ToString("N")[..8];
        ClientSecretToReturn = "cs_test_" + Guid.NewGuid().ToString("N")[..8];
        ErrorCode = string.Empty;
        ErrorMessage = string.Empty;
        RefundShouldSucceed = true;
        RefundIdToReturn = "REF-" + Guid.NewGuid().ToString()[..8];
        ProcessCalls.Clear();
        RefundCalls.Clear();
    }

    protected override void RegisterServices(IServiceCollection s) 
    {
        s.AddSingleton(this);
        s.AddScoped<FakePaymentServiceImpl>();
    }
    
    protected override void MapServices(WebApplication app)
    {
        app.MapGrpcService<FakePaymentServiceImpl>();
    }
}

public class FakePaymentServiceImpl : PaymentService.PaymentServiceBase
{
    private readonly FakePaymentGrpcServer _cfg;
    public FakePaymentServiceImpl(FakePaymentGrpcServer cfg) => _cfg = cfg;

    public override Task<ProcessPaymentResponse> ProcessPayment(
        ProcessPaymentRequest request, 
        ServerCallContext context
        )
    {
        _cfg.ProcessCalls.Add(request);

        if (_cfg.ProcessRpcStatusCodeToThrow.HasValue)
        {
            throw new RpcException(new Status(
                _cfg.ProcessRpcStatusCodeToThrow.Value,
                _cfg.ProcessRpcErrorDetailToThrow));
        }

        if (!_cfg.ProcessShouldSucceed)
            return Task.FromResult(new ProcessPaymentResponse
            {
                Success = false,
                Status = ProcessPaymentStatus.Failed,
                ErrorCode = string.IsNullOrEmpty(_cfg.ErrorCode) ? "PAYMENT_DECLINED" : _cfg.ErrorCode,
                ErrorMessage = string.IsNullOrEmpty(_cfg.ErrorMessage) ? "Card declined" : _cfg.ErrorMessage
            });

        if (_cfg.ProcessStatusToReturn == ProcessPaymentStatus.Failed)
            return Task.FromResult(new ProcessPaymentResponse
            {
                Success = false,
                Status = ProcessPaymentStatus.Failed,
                ErrorCode = string.IsNullOrEmpty(_cfg.ErrorCode) ? "PAYMENT_DECLINED" : _cfg.ErrorCode,
                ErrorMessage = string.IsNullOrEmpty(_cfg.ErrorMessage) ? "Card declined" : _cfg.ErrorMessage
            });

        var isAsyncPending = _cfg.ProcessStatusToReturn is
            ProcessPaymentStatus.Pending or
            ProcessPaymentStatus.RequiresAction;

        return Task.FromResult(new ProcessPaymentResponse
        {
            Success = true,
            PaymentId = _cfg.PaymentIdToReturn,
            Status = _cfg.ProcessStatusToReturn,
            ProviderPaymentIntentId = isAsyncPending ? _cfg.ProviderPaymentIntentIdToReturn : string.Empty,
            ClientSecret = _cfg.ProcessStatusToReturn == ProcessPaymentStatus.RequiresAction
                ? _cfg.ClientSecretToReturn
                : string.Empty,
        });
    }

    public override Task<RefundPaymentResponse> RefundPayment(
        RefundPaymentRequest request,
        ServerCallContext context
        )
    {
        _cfg.RefundCalls.Add(request);

        if (!_cfg.RefundShouldSucceed)
            return Task.FromResult(new RefundPaymentResponse
            {
                Success = false,
                ErrorMessage = "Refund failed"
            });

        return Task.FromResult(new RefundPaymentResponse
        {
            Success = true,
            RefundId = _cfg.RefundIdToReturn
        });
    }
}

//---------Inventory Server---------//

public class FakeInventoryGrpcServer : FakeGrpcServerBase
{
    public bool ReserveShouldSucceed { get; set; } = true;
    public string ReservationIdToReturn { get; set; } = "RES-DEFAULT";
    public string ReserveErrorMessage { get; set; } = string.Empty;
    public bool ReleaseShouldSucceed { get; set; } = true;
    public bool ConfirmShouldSucceed { get; set; } = true;

    public List<ReserveInventoryRequest> ReserveCalls { get; } = new();
    public List<ReleaseInventoryRequest> ReleaseCalls { get; } = new();
    public List<ConfirmReservationRequest> ConfirmCalls { get; } = new();

    public void Reset()
    {
        ReserveShouldSucceed = true;
        ReservationIdToReturn = "RES-" + Guid.NewGuid().ToString()[..8];
        ReserveErrorMessage = string.Empty;
        ReleaseShouldSucceed = true;
        ConfirmShouldSucceed = true;
        ReserveCalls.Clear();
        ReleaseCalls.Clear();
        ConfirmCalls.Clear();
    }

    protected override void RegisterServices(IServiceCollection s) 
    {
        s.AddSingleton(this);
        s.AddScoped<FakeInventoryServiceImpl>();
    }
    
    protected override void MapServices(WebApplication app)
    {
        app.MapGrpcService<FakeInventoryServiceImpl>();
    }
}


public class FakeInventoryServiceImpl : InventoryService.InventoryServiceBase
{
    private readonly FakeInventoryGrpcServer _cfg;
    public FakeInventoryServiceImpl(FakeInventoryGrpcServer cfg) => _cfg = cfg;

    public override Task<ReserveInventoryResponse> ReserveInventory(
        ReserveInventoryRequest request,
        ServerCallContext context
        )
    {
        _cfg.ReserveCalls.Add(request);

        if (!_cfg.ReserveShouldSucceed)
            return Task.FromResult(new ReserveInventoryResponse
            {
                Success = false,
                ErrorMessage = string.IsNullOrEmpty(_cfg.ReserveErrorMessage)
                    ? "Insufficient inventory"
                    : _cfg.ReserveErrorMessage
            });

        return Task.FromResult(new ReserveInventoryResponse
        {
            Success = true,
            ReservationId = _cfg.ReservationIdToReturn
        });
    }

    public override Task<ReleaseInventoryResponse> ReleaseInventory(
        ReleaseInventoryRequest request,
        ServerCallContext context)

    {
        _cfg.ReleaseCalls.Add(request);

        return Task.FromResult(new ReleaseInventoryResponse
        {
            Success = _cfg.ReleaseShouldSucceed,
            ErrorMessage = _cfg.ReleaseShouldSucceed ? string.Empty : "Release failed"
        });
    }

    public override Task<ConfirmReservationResponse> ConfirmReservation(
        ConfirmReservationRequest request,
        ServerCallContext context)
    {
        _cfg.ConfirmCalls.Add(request);

        return Task.FromResult(new ConfirmReservationResponse
        {
            Success = _cfg.ConfirmShouldSucceed,
            ErrorMessage = _cfg.ConfirmShouldSucceed ? string.Empty : "Confirm failed"
        });
    }
}

//---------Accounting Server---------//

public class FakeAccountingGrpcServer : FakeGrpcServerBase
{
    public bool ShouldSucceed { get; set; } = true;
    public string TransactionIdToReturn { get; set; } = "TXN-DEFAULT";
    public string ReversalIdToReturn { get; set; } = "REV_DEFAULT";

    public List<RecordRefundRequest> RecordRefundCalls { get; } = new();
    public List<ReverseRevenueRequest> ReverseRevenueCalls { get; } = new();

    public void Reset()
    {
        ShouldSucceed = true;
        TransactionIdToReturn = "TXN-" + Guid.NewGuid().ToString()[..8];
        ReversalIdToReturn = "REV-" + Guid.NewGuid().ToString()[..8];
        RecordRefundCalls.Clear();
        ReverseRevenueCalls.Clear();
    }

    protected override void RegisterServices(IServiceCollection s) 
    {
        s.AddSingleton(this);
        s.AddScoped<FakeAccountingServiceImpl>();
    }
    
    protected override void MapServices(WebApplication app)
    {
        app.MapGrpcService<FakeAccountingServiceImpl>();
    }
}

public class FakeAccountingServiceImpl : AccountingService.AccountingServiceBase
{
    private readonly FakeAccountingGrpcServer _cfg;
    
    public FakeAccountingServiceImpl(FakeAccountingGrpcServer cfg) => _cfg = cfg;

    public override Task<RecordRefundResponse> RecordRefund(
        RecordRefundRequest request,
        ServerCallContext context
    )
    {
        _cfg.RecordRefundCalls.Add(request);

        return Task.FromResult(new RecordRefundResponse
        {
            Success = _cfg.ShouldSucceed,
            TransactionId = _cfg.ShouldSucceed ? _cfg.TransactionIdToReturn : string.Empty,
            ErrorMessage = _cfg.ShouldSucceed ? string.Empty : "RecordRefund failed"
        });
    }

    public override Task<ReverseRevenueResponse> ReverseRevenue(
        ReverseRevenueRequest request,
        ServerCallContext context
    )
    {
        _cfg.ReverseRevenueCalls.Add(request);

        return Task.FromResult(new ReverseRevenueResponse
        {
            Success = _cfg.ShouldSucceed,
            ReversalId = _cfg.ShouldSucceed ? _cfg.ReversalIdToReturn : string.Empty,
            ErrorMessage = _cfg.ShouldSucceed ? string.Empty : "ReverseRevenue failed"
        });
    }
}


//---------Product Server---------//

public class FakeProductGrpcServer : FakeGrpcServerBase
{
    // price returned for every requested product id
    public bool ShouldSucceed { get; set; } = true;
    public decimal PriceToReturn { get; set; } = 29.99m;
    public string CurrencyToReturn { get; set; } = "USD";
    public string ErrorMessage { get; set; } = string.Empty;

    public List<GetProductPricesRequest> GetPricesCalls { get; } = new();

    public void Reset()
    {
        ShouldSucceed = true;
        PriceToReturn = 29.99m;
        CurrencyToReturn = "USD";
        ErrorMessage = string.Empty;
        GetPricesCalls.Clear();
    }

    protected override void RegisterServices(IServiceCollection s) 
    {
        s.AddSingleton(this);
        s.AddScoped<FakeProductServiceImpl>();
    }
    
    protected override void MapServices(WebApplication app)
    {
        app.MapGrpcService<FakeProductServiceImpl>();
    }
}

public class FakeProductServiceImpl : ProductService.ProductServiceBase
{
    private readonly FakeProductGrpcServer _cfg;
    public FakeProductServiceImpl(FakeProductGrpcServer cfg) => _cfg = cfg;

    public override Task<GetProductPricesResponse> GetProductPrices(
        GetProductPricesRequest request,
        ServerCallContext context)
    {
        _cfg.GetPricesCalls.Add(request);

        if (!_cfg.ShouldSucceed)
        {
            var failResponse = new GetProductPricesResponse();
            failResponse.NotFoundIds.AddRange(request.ProductIds);
            return Task.FromResult(failResponse);
        }

        var response = new GetProductPricesResponse();
        foreach (var productId in request.ProductIds)
        {
            response.Prices.Add(new ProductPrice
            {
                ProductId = productId,
                Price     = ToDecimalValue(_cfg.PriceToReturn),
                Currency  = _cfg.CurrencyToReturn
            });
        }

        return Task.FromResult(response);
    }

    private static DecimalValue ToDecimalValue(decimal value)
    {
        var units = decimal.Truncate(value);
        var nanos = (int)((value - units) * 1_000_000_000m);
        return new DecimalValue { Units = (long)units, Nanos = nanos };
    }
}

