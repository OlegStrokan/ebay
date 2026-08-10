using System.Text;
using System.Text.Json;

namespace OpsConsole.Redaction;

// prevent average goy seeing sensentive data of non-goys or how claude said:
// Masks PII/payment identifiers inside raw JSON blobs before they leave OpsConsole

public static class PiiRedactor
{
    private static readonly string[] SensitiveKeys =
    [
        "email", "phone",
        "customerid", "userid",
        "deliveryaddress", "street", "city", "country", "postalcode",
        "paymentintentid", "providerpaymentintentid", "paymentid",
        "refundid", "reversalid",
        "cardnumber", "cvv"
    ];

    public static string RedactJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedacted(doc.RootElement, writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // Can't inspect what's inside — fail closed rather than pass an unparsable blob through.
            return "[redacted: unparsable payload]";
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsSensitive(prop.Name))
                    {
                        writer.WriteString(prop.Name, "[redacted]");
                    }
                    else
                    {
                        writer.WritePropertyName(prop.Name);
                        WriteRedacted(prop.Value, writer);
                    }
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedacted(item, writer);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string propertyName) =>
        SensitiveKeys.Contains(propertyName, StringComparer.OrdinalIgnoreCase);
}
