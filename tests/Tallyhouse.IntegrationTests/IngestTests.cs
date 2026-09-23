using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.ADO.Readers;
using ClickHouse.Driver.Utility;
using Tallyhouse.Kernel.Ingestion;

namespace Tallyhouse.IntegrationTests;

public sealed class IngestTests(TestRig rig)
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public async Task An_acknowledged_batch_lands_in_the_fact_table_exactly_as_validated()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient client = rig.Client(project.WriteKey);

        JsonElement response = await Events.SendAsync(client,
            Events.Of("page_view", "ada", Now.AddMinutes(-3)),
            Events.Of("signup", "ada", Now.AddMinutes(-2)),
            Events.Of("purchase", "ada", Now.AddMinutes(-1), new { amount = 12.50, currency = "EUR" }));

        response.GetProperty("accepted").GetInt32().ShouldBe(3);
        response.GetProperty("notAccepted").GetArrayLength().ShouldBe(0);

        await rig.WaitForEventsAsync(project.Id, 3);

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", project.Id);

        using ClickHouseDataReader reader = await rig.ClickHouse.ExecuteReaderAsync(
            "SELECT properties['amount'], properties['currency'], user_id, is_late FROM events WHERE project_id = {project:UUID} AND event_name = 'purchase'",
            parameters);

        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("12.5");
        reader.GetString(1).ShouldBe("EUR");
        reader.GetString(2).ShouldBe("ada");
        reader.GetBoolean(3).ShouldBeFalse();
    }

    [Fact]
    public async Task Each_credential_opens_only_its_own_door()
    {
        TestProject project = await rig.NewProjectAsync();
        object batch = new { events = new[] { Events.Of("page_view", "u", Now) } };
        object funnel = new { steps = new[] { "page_view", "signup" }, from = "2026-01-01", to = "2026-01-02", windowSeconds = 3600 };

        using HttpClient anonymous = rig.Client(null);
        using HttpClient writer = rig.Client(project.WriteKey);
        using HttpClient reader = rig.Client(project.ReadKey);

        (await anonymous.PostAsJsonAsync("/v1/events/batch", batch, TestRig.Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await reader.PostAsJsonAsync("/v1/events/batch", batch, TestRig.Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await writer.PostAsJsonAsync("/v1/queries/funnel", funnel, TestRig.Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await writer.PostAsJsonAsync("/v1/projects", new { name = "x" }, TestRig.Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await reader.PostAsJsonAsync("/v1/queries/funnel", funnel, TestRig.Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_client_retry_is_acknowledged_as_a_duplicate_and_stored_once()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient client = rig.Client(project.WriteKey);
        object purchase = Events.Of("purchase", "grace", Now, messageId: "retry-me");

        await Events.SendAsync(client, purchase);
        JsonElement retry = await Events.SendAsync(client, purchase);

        retry.GetProperty("duplicates").GetInt32().ShouldBe(1);
        retry.GetProperty("notAccepted")[0].GetProperty("status").GetString().ShouldBe("duplicate");

        await rig.WaitForEventsAsync(project.Id, 1);
        (await rig.CountAsync(project.Id, final: false)).ShouldBe(1, "the fast layer stopped the retry before the log");
    }

    [Fact]
    public async Task An_invalid_event_is_quarantined_and_replays_once_the_schema_accepts_it()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient writer = rig.Client(project.WriteKey);
        using HttpClient reader = rig.Client(project.ReadKey);
        using HttpClient operatorClient = rig.Client(TestRig.OperatorToken);

        JsonElement response = await Events.SendAsync(writer,
            Events.Of("page_view", "linus", Now, new { path = "/", campaign = "autumn" }),
            Events.Of("page_view", "linus", Now, new { path = "/docs" }));

        response.GetProperty("quarantined").GetInt32().ShouldBe(1);
        JsonElement rejected = response.GetProperty("notAccepted")[0];
        rejected.GetProperty("index").GetInt32().ShouldBe(0);
        rejected.GetProperty("reason").GetString()!.ShouldContain("'campaign' is not declared");

        JsonElement listed = default;
        await TestRig.WaitUntilAsync(async () =>
        {
            listed = await reader.GetFromJsonAsync<JsonElement>("/v1/quarantine", TestRig.Json);
            return listed.GetProperty("items").GetArrayLength() == 1;
        }, TimeSpan.FromSeconds(30));

        Guid quarantineId = listed.GetProperty("items")[0].GetProperty("id").GetGuid();

        HttpResponseMessage stillInvalid = await operatorClient.PostAsync($"/v1/projects/{project.Id}/quarantine/{quarantineId}/replay", null);
        stillInvalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        HttpResponseMessage evolved = await operatorClient.PutAsJsonAsync(
            $"/v1/projects/{project.Id}/schemas/page_view/versions/1",
            new { properties = new { path = new { type = "string", required = true }, referrer = new { type = "string" }, campaign = new { type = "string" } } },
            TestRig.Json);
        evolved.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage replayed = await operatorClient.PostAsync($"/v1/projects/{project.Id}/quarantine/{quarantineId}/replay", null);
        replayed.StatusCode.ShouldBe(HttpStatusCode.OK, await replayed.Content.ReadAsStringAsync());

        await rig.WaitForEventsAsync(project.Id, 2);
        (await rig.CountAsync(project.Id, "properties['campaign'] = 'autumn'")).ShouldBe(1);

        JsonElement after = await reader.GetFromJsonAsync<JsonElement>("/v1/quarantine", TestRig.Json);
        after.GetProperty("items").GetArrayLength().ShouldBe(0, "a replayed event is no longer open");

        (await operatorClient.PostAsync($"/v1/projects/{project.Id}/quarantine/{quarantineId}/replay", null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_breaking_schema_change_is_refused_with_what_it_would_break()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient operatorClient = rig.Client(TestRig.OperatorToken);

        HttpResponseMessage response = await operatorClient.PutAsJsonAsync(
            $"/v1/projects/{project.Id}/schemas/purchase/versions/1",
            new { properties = new { amount = new { type = "integer", required = true } } },
            TestRig.Json);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("changed type");
        body.ShouldContain("'currency' was removed");
    }

    [Fact]
    public async Task An_event_older_than_the_watermark_is_stored_flagged_and_reported_as_late()
    {
        TestProject project = await rig.NewProjectAsync();
        using HttpClient writer = rig.Client(project.WriteKey);
        using HttpClient reader = rig.Client(project.ReadKey);
        DateTimeOffset now = Now;

        await Events.SendAsync(writer,
            Events.Of("page_view", "u1", now.AddMinutes(-5)),
            Events.Of("page_view", "u2", now.AddHours(-3)));

        await rig.WaitForEventsAsync(project.Id, 2);
        (await rig.CountAsync(project.Id, "is_late")).ShouldBe(1);

        DateOnly from = DateOnly.FromDateTime(now.AddDays(-1).UtcDateTime);
        DateOnly to = DateOnly.FromDateTime(now.UtcDateTime);

        HttpResponseMessage response = await reader.PostAsJsonAsync("/v1/queries/segment", new { @event = "page_view", from, to }, TestRig.Json);
        JsonElement segment = await response.Content.ReadFromJsonAsync<JsonElement>(TestRig.Json);

        segment.GetProperty("lateEvents").GetInt64().ShouldBe(1);
        segment.GetProperty("days").EnumerateArray().Sum(day => day.GetProperty("events").GetInt64()).ShouldBe(1);
    }

    [Fact]
    public async Task A_sampled_event_type_keeps_whole_users_and_queries_scale_them_back()
    {
        TestProject project = await rig.NewProjectAsync(new { redactProperties = Array.Empty<string>(), sampleRates = new Dictionary<string, double> { ["page_view"] = 0.1 } });
        using HttpClient writer = rig.Client(project.WriteKey);
        using HttpClient reader = rig.Client(project.ReadKey);
        DateTimeOffset now = Now;
        string[] users = [.. Enumerable.Range(0, 2000).Select(i => $"visitor-{i}")];

        int sampledOut = 0;

        foreach (string[] chunk in users.Chunk(250))
        {
            // Two page views per user: a kept user must be kept for both, a dropped user for neither.
            JsonElement response = await Events.SendAsync(writer, [.. chunk.SelectMany(user => new[]
            {
                Events.Of("page_view", user, now.AddMinutes(-2)),
                Events.Of("page_view", user, now.AddMinutes(-1)),
            })]);

            sampledOut += response.GetProperty("sampledOut").GetInt32();
        }

        int kept = users.Count(user => Sampling.Keeps(Sampling.BucketOf(UserIdentity.KeyOf(user, null)), Sampling.ThresholdOf(0.1)));
        sampledOut.ShouldBe((users.Length - kept) * 2);

        await rig.WaitForEventsAsync(project.Id, kept * 2);
        (await rig.CountAsync(project.Id, "1")).ShouldBe(kept * 2, "no user is split between kept and dropped");

        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        HttpResponseMessage segment = await reader.PostAsJsonAsync("/v1/queries/segment", new { @event = "page_view", from = today.AddDays(-1), to = today }, TestRig.Json);
        JsonElement result = await segment.Content.ReadFromJsonAsync<JsonElement>(TestRig.Json);

        result.GetProperty("sampling").GetProperty("threshold").GetInt32().ShouldBe(1000);
        result.GetProperty("days").EnumerateArray().Sum(day => day.GetProperty("events").GetInt64()).ShouldBe(kept * 2 * 10);
        long estimatedUsers = result.GetProperty("days").EnumerateArray().Sum(day => day.GetProperty("users").GetInt64());
        estimatedUsers.ShouldBe(kept * 10);
        ((double)estimatedUsers / users.Length).ShouldBe(1.0, 0.2, "an estimate from a 10% sample of 2,000 users");
    }

    [Fact]
    public async Task Personal_data_configured_for_redaction_never_reaches_the_store()
    {
        TestProject project = await rig.NewProjectAsync(new { redactProperties = new[] { "email" }, sampleRates = new { } });
        using HttpClient writer = rig.Client(project.WriteKey);

        await Events.SendAsync(writer, Events.Of("purchase", "ada", Now, new { amount = 5, email = "ada@example.com" }));

        await rig.WaitForEventsAsync(project.Id, 1);
        (await rig.CountAsync(project.Id, "properties['email'] = '[redacted]'")).ShouldBe(1);
        (await rig.CountAsync(project.Id, "positionCaseInsensitive(toString(properties), 'ada@example.com') > 0")).ShouldBe(0);
    }
}
