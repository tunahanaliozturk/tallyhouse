using System.Buffers;
using System.Text;
using System.Text.Json;
using Tallyhouse.Domain.Schemas;

namespace Tallyhouse.Application.Ingestion;

/// <summary>
/// The copy of a rejected event that quarantine keeps. It is the event as received, because the point of
/// quarantine is to show somebody exactly what the client sent, with one exception: configured personal
/// data is redacted here too. An invalid event is no less personal than a valid one.
/// </summary>
public static class QuarantinePayload
{
    /// <summary>Larger payloads are not kept; the reason says so. A client sending these has a bug, not data.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <returns>The payload to keep, or null when it is larger than <see cref="MaxBytes"/>.</returns>
    public static string? Write(JsonElement raw, IReadOnlySet<string> redact)
    {
        ArgumentNullException.ThrowIfNull(redact);

        ArrayBufferWriter<byte> buffer = new(1024);

        using (Utf8JsonWriter writer = new(buffer))
        {
            if (raw.ValueKind != JsonValueKind.Object || redact.Count == 0)
            {
                raw.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();

                foreach (JsonProperty property in raw.EnumerateObject())
                {
                    if (property.NameEquals("properties") && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName(property.Name);
                        WriteRedacted(writer, property.Value, redact);
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }
        }

        return buffer.WrittenCount <= MaxBytes ? Encoding.UTF8.GetString(buffer.WrittenSpan) : null;
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement properties, IReadOnlySet<string> redact)
    {
        writer.WriteStartObject();

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (redact.Contains(property.Name))
            {
                writer.WriteString(property.Name, CompiledSchema.RedactedMarker);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
