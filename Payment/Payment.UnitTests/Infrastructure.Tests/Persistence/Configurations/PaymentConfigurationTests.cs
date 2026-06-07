using Domain.Entities;
using Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Persistence.Configurations;

public class PaymentConfigurationTests
{
    [Fact]
    public void Payment_ShouldUseXminAsConcurrencyToken()
    {
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        using var context = new PaymentDbContext(options);

        var paymentEntity = context.Model.FindEntityType(typeof(Payment));
        Assert.NotNull(paymentEntity);

        var xminProperty = paymentEntity!.FindProperty("xmin");
        Assert.NotNull(xminProperty);
        Assert.True(xminProperty!.IsConcurrencyToken);
    }
}
