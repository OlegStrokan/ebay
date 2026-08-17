using System.Text.Json;
using Application.Contracts;

namespace Infrastructure.Messaging;

// Payment serializes money-events with a camelCase naming policy; case-insensitive matching
// reads those onto the PascalCase record without a second naming policy to keep in step.
internal static class MoneyEventPayloadParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Returns false for anything that is not a usable money-event rather than throwing: the
    // caller has to decide what to do with a bad message, and a malformed payload is a
    // permanent condition, not a transient one.
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
