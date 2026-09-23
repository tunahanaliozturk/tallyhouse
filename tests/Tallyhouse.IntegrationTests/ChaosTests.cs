using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tallyhouse.IntegrationTests;

/// <summary>
/// The two failure modes the design is built around, induced for real by pausing containers: the analytics
/// store disappearing, which must cost freshness and nothing else, and the log disappearing, which must stop
/// acknowledgements rather than acknowledge events nobody kept.
/// </summary>
public sealed class ChaosTests(TestRig rig)
{
    [Fact]
    public async Task With_ClickHouse_down_ingest_keeps_acknowledging_and_nothing_acknowledged_is_lost()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient writer = rig.Client(project.WriteKey);
        int acknowledged = 0;

        await rig.ClickHouseContainer.PauseAsync();

        try
        {
            for (int batch = 0; batch < 20; batch++)
            {
                object[] events = [.. Enumerable.Range(0, 50).Select(i => Events.Of("page_view", $"u{(batch * 50) + i}", DateTimeOffset.UtcNow))];
                JsonElement response = await Events.SendAsync(writer, events);
                acknowledged += response.GetProperty("accepted").GetInt32();
            }
        }
        finally
        {
            await rig.ClickHouseContainer.UnpauseAsync();
        }

        acknowledged.ShouldBe(1000);

        // The loader has been retrying one batch with backoff the whole time. Once ClickHouse answers again it
        // writes that batch and drains the rest from Kafka.
        await rig.WaitForEventsAsync(project.Id, 1000, TimeSpan.FromSeconds(120));
        (await rig.CountAsync(project.Id)).ShouldBe(1000);
    }

    [Fact]
    public async Task With_Kafka_down_nothing_is_acknowledged_and_the_retry_is_stored_once()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient writer = rig.Client(project.WriteKey);
        object batch = new { events = Enumerable.Range(0, 10).Select(i => Events.Of("signup", $"u{i}", DateTimeOffset.UtcNow, messageId: $"kafka-down-{i}")).ToArray() };

        await rig.Kafka.PauseAsync();

        try
        {
            HttpResponseMessage refused = await writer.PostAsJsonAsync("/v1/events/batch", batch, TestRig.Json);

            refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            refused.Headers.RetryAfter.ShouldNotBeNull();
        }
        finally
        {
            await rig.Kafka.UnpauseAsync();
        }

        HttpResponseMessage retried = null!;

        // The producer reconnects on its own schedule after the broker comes back.
        await TestRig.WaitUntilAsync(async () =>
        {
            retried = await writer.PostAsJsonAsync("/v1/events/batch", batch, TestRig.Json);
            return retried.StatusCode == HttpStatusCode.Accepted;
        }, TimeSpan.FromSeconds(60));

        await rig.WaitForEventsAsync(project.Id, 10, TimeSpan.FromSeconds(60));
        (await rig.CountAsync(project.Id)).ShouldBe(10, "whatever the refused attempt left in the log is the same ten events");
    }
}
