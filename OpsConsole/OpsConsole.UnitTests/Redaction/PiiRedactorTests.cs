using OpsConsole.Redaction;

namespace OpsConsole.UnitTests.Redaction;

public class PiiRedactorTests
{
    [Fact]
    public void RedactJson_ShouldMaskSensitiveTopLevelFields()
    {
        var raw = """{"CustomerId":"cust-1","TotalAmount":59.98}""";

        var result = PiiRedactor.RedactJson(raw);

        Assert.Contains("\"CustomerId\":\"[redacted]\"", result);
        Assert.Contains("\"TotalAmount\":59.98", result);
    }

    [Fact]
    public void RedactJson_ShouldMaskSensitiveContainerField_AsAWhole()
    {
        // DeliveryAddress is itself a sensitive key, so the whole nested object is blanked
        // rather than partially redacted field-by-field — simpler and strictly safer.
        var raw = """{"DeliveryAddress":{"Street":"123 Main St","City":"Metropolis"},"Items":[{"ProductId":"p1"}]}""";

        var result = PiiRedactor.RedactJson(raw);

        Assert.Contains("\"DeliveryAddress\":\"[redacted]\"", result);
        Assert.DoesNotContain("123 Main St", result);
        Assert.Contains("\"ProductId\":\"p1\"", result);
    }

    [Fact]
    public void RedactJson_ShouldMaskUnwrappedAddressFields_WhenNotNestedUnderASensitiveKey()
    {
        var raw = """{"Street":"123 Main St","City":"Metropolis","ProductId":"p1"}""";

        var result = PiiRedactor.RedactJson(raw);

        Assert.Contains("\"Street\":\"[redacted]\"", result);
        Assert.Contains("\"City\":\"[redacted]\"", result);
        Assert.Contains("\"ProductId\":\"p1\"", result);
    }

    [Fact]
    public void RedactJson_ShouldMaskPaymentIdentifiers_CaseInsensitively()
    {
        var raw = """{"paymentIntentId":"pi_123","providerPaymentIntentId":"pi_456"}""";

        var result = PiiRedactor.RedactJson(raw);

        Assert.DoesNotContain("pi_123", result);
        Assert.DoesNotContain("pi_456", result);
    }

    [Fact]
    public void RedactJson_ShouldFailClosed_WhenPayloadIsNotValidJson()
    {
        var result = PiiRedactor.RedactJson("not json at all");

        Assert.Equal("[redacted: unparsable payload]", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RedactJson_ShouldReturnAsIs_WhenEmptyOrWhitespace(string? raw)
    {
        var result = PiiRedactor.RedactJson(raw);

        Assert.Equal(raw ?? string.Empty, result);
    }
}
