using System.Text.Json;
using Application.Contracts;

namespace Infrastructure.Messaging;

internal static class MoneyEventPayloadParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryParse(string? json, out MoneyEventPayload? payload, out string? error)
    {
        payload = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Message value is empty.";
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<MoneyEventPayload>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            error = $"Message value is not valid money-event JSON: {ex.Message}";
            return false;
        }

        if (payload is null)
        {
            error = "Message value deserialized to null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.EventId))
        {
            error = "Money event carries no eventId, so it cannot be de-duplicated.";
            payload = null;
            return false;
        }

        return true;
    }
}
