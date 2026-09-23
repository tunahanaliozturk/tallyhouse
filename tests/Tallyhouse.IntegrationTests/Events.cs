using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tallyhouse.IntegrationTests;

/// <summary>Builds events the way an SDK sends them.</summary>
public static class Events
{
    public static object Of(string name, string user, DateTimeOffset at, object? properties = null, string? messageId = null) => new
    {
        messageId = messageId ?? Guid.NewGuid().ToString("N"),
        @event = name,
        userId = user,
        timestamp = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        properties = properties ?? DefaultProperties(name),
    };

    public static object DefaultProperties(string name) => name switch
    {
        "page_view" => new { path = "/pricing" },
        "signup" => new { plan = "free" },
        "purchase" => new { amount = 19.9, currency = "EUR" },
        _ => new { },
    };

    public static async Task<JsonElement> SendAsync(HttpClient client, params object[] events)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/events/batch", new { events }, TestRig.Json);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestRig.Json);
    }
}
