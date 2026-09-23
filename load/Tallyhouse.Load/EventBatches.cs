using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tallyhouse.Load;

internal sealed record Event(string MessageId, string Name, string User, DateTimeOffset At, IReadOnlyList<(string Key, object Value)> Properties);

internal readonly record struct Delivery(HttpStatusCode Status, int Accepted, int Duplicates, int SampledOut, int Quarantined);

/// <summary>Writes batches the way an SDK does and sends them with a project's write key.</summary>
internal static class EventBatches
{
    public static ByteArrayContent Body(IReadOnlyList<Event> events)
    {
        ArrayBufferWriter<byte> buffer = new(events.Count * 256);

        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("events");

            foreach (Event e in events)
            {
                json.WriteStartObject();
                json.WriteString("messageId", e.MessageId);
                json.WriteString("event", e.Name);
                json.WriteString("userId", e.User);
                json.WriteString("timestamp", e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                json.WriteStartObject("properties");

                foreach ((string key, object value) in e.Properties)
                {
                    switch (value)
                    {
                        case string text:
                            json.WriteString(key, text);
                            break;
                        case double number:
                            json.WriteNumber(key, number);
                            break;
                        default:
                            json.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                            break;
                    }
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        ByteArrayContent content = new(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    public static async Task<Delivery> SendAsync(HttpClient http, string writeKey, IReadOnlyList<Event> events, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/events/batch") { Content = Body(events) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writeKey);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return new Delivery(response.StatusCode, 0, 0, 0, 0);
        }

        using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        JsonElement root = body.RootElement;

        return new Delivery(
            response.StatusCode,
            root.GetProperty("accepted").GetInt32(),
            root.GetProperty("duplicates").GetInt32(),
            root.GetProperty("sampledOut").GetInt32(),
            root.GetProperty("quarantined").GetInt32());
    }

    public static Event PageView(string messageId, string user, DateTimeOffset at, int variant) =>
        new(messageId, "page_view", user, at, [("path", Paths[variant % Paths.Length])]);

    private static readonly string[] Paths = ["/", "/pricing", "/docs", "/blog", "/signup", "/features"];
}
