using Application.Gateways;
using Application.Gateways.Models;

namespace Infrastructure.Gateways;

internal sealed class MockFintechPaymentProvider(
    ILogger<MockFintechPaymentProvider> logger) : IStripePaymentProvider
{
    public Task<ProcessPaymentProviderResult> ProcessPaymentAsync(
        ProcessPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("MockFintechPaymentProvider.ProcessPaymentAsync is not yet implemented.");
        throw new NotImplementedException("MockFintech provider is not yet implemented. Build your fintech REST API first.");
    }

    public Task<RefundPaymentProviderResult> RefundPaymentAsync(
        RefundPaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("MockFintechPaymentProvider.RefundPaymentAsync is not yet implemented.");
        throw new NotImplementedException("MockFintech provider is not yet implemented.");
    }

    public Task<CapturePaymentProviderResult> CapturePaymentAsync(
        CapturePaymentProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("MockFintechPaymentProvider.CapturePaymentAsync is not yet implemented.");
        throw new NotImplementedException("MockFintech provider is not yet implemented.");
    }

    public Task<ProviderPaymentStatusResult> GetPaymentStatusAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("MockFintechPaymentProvider.GetPaymentStatusAsync is not yet implemented.");
        throw new NotImplementedException("MockFintech provider is not yet implemented.");
    }

    public Task<ProviderRefundStatusResult> GetRefundStatusAsync(
        string providerRefundId,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("MockFintechPaymentProvider.GetRefundStatusAsync is not yet implemented.");
        throw new NotImplementedException("MockFintech provider is not yet implemented.");
    }

    public Task CancelAuthorizationAsync(
        string providerPaymentIntentId,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("MockFintechPaymentProvider.CancelAuthorizationAsync is not yet implemented.");
        throw new NotImplementedException("MockFintech provider is not yet implemented.");
    }
}
