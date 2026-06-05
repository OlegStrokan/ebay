using System.Text.Json;
using Application.Common.Enums;
using Application.Gateways;
using Infrastructure.Gateways;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Order.IntegrationTests.Gateways;

public sealed class TelegramIncidentReporterIntegrationTests
{
    [Fact]
    public async Task SendAlertAsync_ShouldCallRealTelegramApi_WhenCredentialsProvided()
    {
        var options = LoadOptions();

        if (!options.Enabled
            || string.IsNullOrWhiteSpace(options.BotToken)
            || string.IsNullOrWhiteSpace(options.ChatId))
        {
            return;
        }

        using var httpClient = new HttpClient();
        var logger = new RecordingLogger<TelegramIncidentReporter>();
        var reporter = new TelegramIncidentReporter(httpClient, Options.Create(options), logger);

        await reporter.SendAlertAsync(
            new IncidentAlert(
                AlertType: "TelegramIntegrationSmokeTest",
                OrderId: Guid.NewGuid(),
                RefundId: null,
                Message: $"integration test ping {Guid.NewGuid():N}",
                Severity: AlertSeverity.Critical),
            CancellationToken.None);

        var errors = logger.Entries
            .Where(e => e.Level >= LogLevel.Error)
            .Select(e => e.Message)
            .ToList();

        Assert.True(
            errors.Count == 0,
            $"Telegram integration test logged errors: {string.Join(" | ", errors)}");
    }

    private static TelegramIncidentReporterOptions LoadOptions()
    {
        var options = new TelegramIncidentReporterOptions();

        ApplyFromFile(options, Path.Combine(AppContext.BaseDirectory, "appsettings.telegram.integration.json"));
        ApplyFromFile(options, Path.Combine(AppContext.BaseDirectory, "appsettings.telegram.integration.local.json"));
        ApplyFromEnvironment(options);

        return options;
    }

    private static void ApplyFromFile(TelegramIncidentReporterOptions options, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("IncidentReporter", out var incidentReporter))
        {
            return;
        }

        if (!incidentReporter.TryGetProperty("Telegram", out var telegram))
        {
            return;
        }

        if (telegram.TryGetProperty("Enabled", out var enabledProp) && enabledProp.ValueKind == JsonValueKind.True)
        {
            options.Enabled = true;
        }
        else if (telegram.TryGetProperty("Enabled", out enabledProp) && enabledProp.ValueKind == JsonValueKind.False)
        {
            options.Enabled = false;
        }

        if (telegram.TryGetProperty("BotToken", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
        {
            options.BotToken = tokenProp.GetString() ?? options.BotToken;
        }

        if (telegram.TryGetProperty("ChatId", out var chatIdProp) && chatIdProp.ValueKind == JsonValueKind.String)
        {
            options.ChatId = chatIdProp.GetString() ?? options.ChatId;
        }

        if (telegram.TryGetProperty("TimeoutSeconds", out var timeoutProp) && timeoutProp.TryGetInt32(out var timeout))
        {
            options.TimeoutSeconds = timeout;
        }
    }

    private static void ApplyFromEnvironment(TelegramIncidentReporterOptions options)
    {
        var enabled = Environment.GetEnvironmentVariable("ORDER_TELEGRAM_TEST_ENABLED");
        if (bool.TryParse(enabled, out var parsedEnabled))
        {
            options.Enabled = parsedEnabled;
        }

        var token = Environment.GetEnvironmentVariable("ORDER_TELEGRAM_TEST_BOTTOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            options.BotToken = token;
        }

        var chatId = Environment.GetEnvironmentVariable("ORDER_TELEGRAM_TEST_CHATID");
        if (!string.IsNullOrWhiteSpace(chatId))
        {
            options.ChatId = chatId;
        }

        var timeout = Environment.GetEnvironmentVariable("ORDER_TELEGRAM_TEST_TIMEOUTSECONDS");
        if (int.TryParse(timeout, out var parsedTimeout))
        {
            options.TimeoutSeconds = parsedTimeout;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel Level, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
