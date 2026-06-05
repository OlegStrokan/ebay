namespace Infrastructure.Gateways;

public sealed class TelegramIncidentReporterOptions
{
    public const string SectionName = "IncidentReporter:Telegram";

    public bool Enabled { get; set; } = false;

    public string BotToken { get; set; } = string.Empty;

    public string ChatId { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;
}
