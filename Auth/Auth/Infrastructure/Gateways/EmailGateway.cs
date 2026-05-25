using System.Text.Json;
using Application.Common.Interfaces;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Gateways;

public class EmailGateway(IConfiguration configuration, ILogger<EmailGateway> logger) : IEmailGateway
{
    private const string VerificationEventType = "EmailVerificationRequested";
    private const string PasswordResetEventType = "PasswordResetRequested";

    public async Task SendVerificationEmailAsync(string recipientEmail, string verificationCode, CancellationToken cancellationToken = default)
    {
        var fromAddress = configuration["Email:FromAddress"] ?? "no-reply@free-ebay.com";

        var payload = new EmailVerificationRequested(
            Guid.NewGuid(),
            recipientEmail,
            fromAddress,
            "Your verification code",
            BuildVerificationBody(verificationCode),
            IsImportant: true,
            DateTime.UtcNow);

        await PublishAsync(VerificationEventType, recipientEmail, payload, cancellationToken);

        logger.LogInformation("Published verification email event for {Email}", recipientEmail);
    }

    public async Task SendPasswordResetEmailAsync(string recipientEmail, string resetToken, CancellationToken cancellationToken = default)
    {
        var frontendUrl = configuration["App:FrontendUrl"] ?? "http://localhost:3000";
        var resetLink = $"{frontendUrl}/reset-password?token={resetToken}";
        var fromAddress = configuration["Email:FromAddress"] ?? "no-reply@free-ebay.com";

        var payload = new PasswordResetRequested(
            Guid.NewGuid(),
            recipientEmail,
            fromAddress,
            "Reset your password",
            BuildPasswordResetBody(resetLink),
            IsImportant: true,
            DateTime.UtcNow);

        await PublishAsync(PasswordResetEventType, recipientEmail, payload, cancellationToken);

        logger.LogInformation("Published password reset email event for {Email}", recipientEmail);
    }

    private async Task PublishAsync<T>(string eventType, string key, T payload, CancellationToken cancellationToken)
    {
        var bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
        var topic = configuration["Kafka:EmailEventsTopic"] ?? "email.events";

        var wrapper = new
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Payload = payload,
            OccurredOn = DateTime.UtcNow
        };

        var config = new ProducerConfig { BootstrapServers = bootstrapServers };

        using var producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => logger.LogError("Kafka producer error: {Reason}", error.Reason))
            .Build();

        try
        {
            await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = key,
                Value = JsonSerializer.Serialize(wrapper)
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // Email dispatch must never fail the primary flow - log and swallow
            logger.LogError(ex, "Failed to publish {EventType} email event for {Key}", eventType, key);
        }
    }

    private static string BuildVerificationBody(string verificationCode) =>
        $"""
        <h2>Verify your email address</h2>
        <p>Use the code below to verify your email address. The code expires in <strong>10 minutes</strong>.</p>
        <p style="font-size:32px;font-weight:bold;letter-spacing:8px;text-align:center;">{verificationCode}</p>
        """;  

    private static string BuildPasswordResetBody(string resetLink) =>
        $"""
        <h2>Reset your password</h2>
        <p>Click the button below to reset your password. The link expires in <strong>1 hour</strong>.</p>
        <p><a href="{resetLink}" style="padding:10px 20px;background:#2563eb;color:#fff;text-decoration:none;border-radius:4px;">Reset Password</a></p>
        <p>Or copy this link into your browser:<br/><code>{resetLink}</code></p>
        <p>If you didn't request a password reset, you can ignore this email.</p>
        """;

    private sealed record EmailVerificationRequested(
        Guid MessageId,
        string To,
        string From,
        string Subject,
        string HtmlBody,
        bool IsImportant,
        DateTime RequestedAtUtc);

    private sealed record PasswordResetRequested(
        Guid MessageId,
        string To,
        string From,
        string Subject,
        string HtmlBody,
        bool IsImportant,
        DateTime RequestedAtUtc);
}
