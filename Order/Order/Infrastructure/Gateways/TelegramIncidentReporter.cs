using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Application.Gateways;
using Microsoft.Extensions.Options;

namespace Infrastructure.Gateways;

// Future implementations can include JiraIncidentReporter, PagerDutyIncidentReporter, SlackIncidentReporter and shit
public sealed class TelegramIncidentReporter(
    HttpClient httpClient,
    IOptions<TelegramIncidentReporterOptions> options,
    ILogger<TelegramIncidentReporter> logger) : IIncidentReporter
{
    private const int TelegramMessageMaxLength = 3900;
    private readonly TelegramIncidentReporterOptions _options = options.Value;

    public async Task SendAlertAsync(IncidentAlert alert, CancellationToken cancellationToken)
    {
        var message = BuildAlertMessage(alert);
        await SendMessageAsync(message, cancellationToken);
    }

    public async Task CreateInterventionTicketAsync(InterventionTicket ticket, CancellationToken cancellationToken)
    {
        var message = BuildInterventionMessage(ticket);
        await SendMessageAsync(message, cancellationToken);
    }

    private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning("Telegram incident reporter is disabled. Incident notification was skipped.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
        {
            logger.LogError(
                "Telegram incident reporter is enabled but BotToken/ChatId is missing. Configure {Section}.",
                TelegramIncidentReporterOptions.SectionName);
            return;
        }

        var endpoint = $"https://api.telegram.org/bot{_options.BotToken}/sendMessage";
        var payload = new TelegramSendMessageRequest(
            ChatId: _options.ChatId,
            Text: Truncate(message, TelegramMessageMaxLength));

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var response = await httpClient.PostAsJsonAsync(endpoint, payload, linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
            logger.LogError(
                "Failed to send Telegram incident notification. StatusCode={StatusCode}, Response={Response}",
                (int)response.StatusCode,
                Truncate(responseBody, 1000));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogError(
                "Telegram incident notification timed out after {TimeoutSeconds} seconds.",
                _options.TimeoutSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while sending Telegram incident notification.");
        }
    }

    private static string BuildAlertMessage(IncidentAlert alert)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ORDER INCIDENT ALERT]");
        sb.Append("Severity: ").AppendLine(alert.Severity.ToString());
        sb.Append("Type: ").AppendLine(alert.AlertType);
        sb.Append("OrderId: ").AppendLine(alert.OrderId.ToString());

        if (!string.IsNullOrWhiteSpace(alert.RefundId))
        {
            sb.Append("RefundId: ").AppendLine(alert.RefundId);
        }

        sb.Append("Message: ").AppendLine(alert.Message);
        sb.Append("TimestampUtc: ").AppendLine(DateTime.UtcNow.ToString("O"));

        return sb.ToString();
    }

    private static string BuildInterventionMessage(InterventionTicket ticket)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ORDER MANUAL INTERVENTION]");
        sb.Append("OrderId: ").AppendLine(ticket.OrderId.ToString());

        if (!string.IsNullOrWhiteSpace(ticket.RefundId))
        {
            sb.Append("RefundId: ").AppendLine(ticket.RefundId);
        }

        sb.Append("Issue: ").AppendLine(ticket.Issue);
        sb.Append("SuggestedAction: ").AppendLine(ticket.SuggestedAction);
        sb.Append("TimestampUtc: ").AppendLine(DateTime.UtcNow.ToString("O"));

        return sb.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private sealed record TelegramSendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text);
}
