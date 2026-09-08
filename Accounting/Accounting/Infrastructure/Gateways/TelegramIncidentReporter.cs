using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Application.Gateways;
using Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Infrastructure.Gateways;

public sealed class TelegramIncidentReporter(
    HttpClient httpClient,
    IOptions<TelegramIncidentReporterOptions> options,
    ILogger<TelegramIncidentReporter> logger) : IIncidentReporter
{
    private const int TelegramMessageMaxLength = 3900;
    private readonly TelegramIncidentReporterOptions _options = options.Value;

    public async Task ReportAsync(LedgerIncident incident, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning(
                "Telegram incident reporter is disabled. Ledger incident {AlertType} was not delivered: {Summary}",
                incident.AlertType,
                incident.Summary);
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
            Text: Truncate(BuildMessage(incident), TelegramMessageMaxLength));

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var response = await httpClient.PostAsJsonAsync(endpoint, payload, linkedCts.Token);

            if (response.IsSuccessStatusCode)
                return;

            var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
            logger.LogError(
                "Failed to send Telegram ledger incident. StatusCode={StatusCode}, Response={Response}",
                (int)response.StatusCode,
                Truncate(responseBody, 1000));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogError(
                "Telegram ledger incident notification timed out after {TimeoutSeconds} seconds.",
                _options.TimeoutSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while sending the Telegram ledger incident notification.");
        }
    }

    private static string BuildMessage(LedgerIncident incident)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ACCOUNTING LEDGER INCIDENT]");
        sb.Append("Severity: ").AppendLine(incident.Severity.ToString());
        sb.Append("Type: ").AppendLine(incident.AlertType);
        sb.Append("Summary: ").AppendLine(incident.Summary);

        if (incident.Details.Count > 0)
        {
            sb.AppendLine("Details:");
            foreach (var detail in incident.Details)
                sb.Append("- ").AppendLine(detail);
        }

        sb.Append("TimestampUtc: ").AppendLine(DateTime.UtcNow.ToString("O"));
        return sb.ToString();
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private sealed record TelegramSendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text);
}
