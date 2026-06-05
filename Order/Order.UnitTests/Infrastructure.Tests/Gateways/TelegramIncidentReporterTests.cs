using System.Net;
using System.Text.Json;
using Application.Common.Enums;
using Application.Gateways;
using Infrastructure.Gateways;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Gateways;

public sealed class TelegramIncidentReporterTests
{
    [Fact]
    public async Task SendAlertAsync_ShouldNotCallApi_WhenReporterDisabled()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var reporter = BuildReporter(client, enabled: false, botToken: "token", chatId: "123");

        await reporter.SendAlertAsync(
            new IncidentAlert(
                AlertType: "PaymentRefundCompensationRetryExhausted",
                OrderId: Guid.NewGuid(),
                RefundId: null,
                Message: "disabled reporter test",
                Severity: AlertSeverity.Critical),
            CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAlertAsync_ShouldNotCallApi_WhenTokenOrChatIdMissing()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var reporter = BuildReporter(client, enabled: true, botToken: "", chatId: "");

        await reporter.SendAlertAsync(
            new IncidentAlert(
                AlertType: "PaymentRefundCompensationRetryExhausted",
                OrderId: Guid.NewGuid(),
                RefundId: null,
                Message: "misconfigured reporter test",
                Severity: AlertSeverity.Critical),
            CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAlertAsync_ShouldPostExpectedPayload_WhenConfigured()
    {
        const string botToken = "bot-token-123";
        const string chatId = "987654321";

        using var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var reporter = BuildReporter(client, enabled: true, botToken: botToken, chatId: chatId);

        var orderId = Guid.NewGuid();
        await reporter.SendAlertAsync(
            new IncidentAlert(
                AlertType: "PaymentRefundCompensationRetryExhausted",
                OrderId: orderId,
                RefundId: "ref-1",
                Message: "retry exhausted",
                Severity: AlertSeverity.Critical),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.telegram.org/bot{botToken}/sendMessage", request.Url?.ToString());

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(chatId, root.GetProperty("chat_id").GetString());

        var text = root.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains("[ORDER INCIDENT ALERT]", text);
        Assert.Contains("PaymentRefundCompensationRetryExhausted", text);
        Assert.Contains(orderId.ToString(), text);
        Assert.Contains("retry exhausted", text);
    }

    [Fact]
    public async Task CreateInterventionTicketAsync_ShouldTruncateTooLongMessages()
    {
        using var handler = new RecordingHttpMessageHandler();
        using var client = new HttpClient(handler);
        var reporter = BuildReporter(client, enabled: true, botToken: "token", chatId: "123");

        var longIssue = new string('x', 5000);
        var longAction = new string('y', 5000);

        await reporter.CreateInterventionTicketAsync(
            new InterventionTicket(
                OrderId: Guid.NewGuid(),
                RefundId: "ref-1",
                Issue: longIssue,
                SuggestedAction: longAction),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        var text = body.RootElement.GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.True(text!.Length <= 3900);
        Assert.Contains("[ORDER MANUAL INTERVENTION]", text);
    }

    private static TelegramIncidentReporter BuildReporter(
        HttpClient httpClient,
        bool enabled,
        string botToken,
        string chatId,
        int timeoutSeconds = 5)
    {
        var options = Options.Create(new TelegramIncidentReporterOptions
        {
            Enabled = enabled,
            BotToken = botToken,
            ChatId = chatId,
            TimeoutSeconds = timeoutSeconds,
        });

        return new TelegramIncidentReporter(
            httpClient,
            options,
            NullLogger<TelegramIncidentReporter>.Instance);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Url, string Body);
}
