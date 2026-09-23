using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Tallyhouse.Application.Ingestion;
using Tallyhouse.Application.Queries;
using Tallyhouse.Domain.Events;
using Tallyhouse.Domain.Projects;
using Tallyhouse.Domain.Schemas;
using Tallyhouse.Infrastructure.ClickHouse;

namespace Tallyhouse.Api;

/// <summary>
/// What an ingest request did. Counts for everything, and one entry per event that was not stored as a new
/// event, by its position in the request. A batch of 500 accepted events answers with four numbers, not
/// 500 objects.
/// </summary>
internal sealed record IngestResponse(int Accepted, int Duplicates, int SampledOut, int Quarantined, IReadOnlyList<EventResultResponse> NotAccepted)
{
    public static IngestResponse From(IngestResult result)
    {
        List<EventResultResponse> notAccepted = [];

        for (int i = 0; i < result.Outcomes.Count; i++)
        {
            EventOutcome outcome = result.Outcomes[i];

            if (outcome.Status != EventStatus.Accepted)
            {
                notAccepted.Add(new EventResultResponse(i, outcome.MessageId, IngestPipeline.StatusTag(outcome.Status), outcome.Reason));
            }
        }

        return new IngestResponse(
            result.Count(EventStatus.Accepted),
            result.Count(EventStatus.Duplicate),
            result.Count(EventStatus.SampledOut),
            result.Count(EventStatus.Quarantined),
            notAccepted);
    }
}

internal sealed record EventResultResponse(int Index, string? MessageId, string Status, string? Reason);

internal sealed record CreateProjectRequest(string? Name, ProjectSettings? Settings);

internal sealed record CreateProjectResponse(Guid Id, string Name, string WriteKey, string ReadKey);

internal sealed record SchemaResponse(string Event, int Version, SchemaSpec Spec);

internal sealed record PutSchemaResponse(string Event, int Version, string Change);

internal sealed record ReplayResponse(Guid QuarantineId, string Status, string? Reason);

internal sealed record QuarantineItemResponse(Guid Id, DateTimeOffset ReceivedAt, string MessageId, string Event, string Reason, string Payload);

internal sealed record QuarantinePageResponse(IReadOnlyList<QuarantineItemResponse> Items, string? Next)
{
    public static QuarantinePageResponse From(QuarantinePage page) => new(
        [.. page.Items.Select(item => new QuarantineItemResponse(item.QuarantineId, item.ReceivedAt, item.MessageId, item.EventName, item.Reason, item.Payload))],
        page.Next);
}

// Nulls are written, not omitted. The OpenAPI document describes a nullable field as present and null, and
// the dashboard's types are generated from that document, so leaving the field out would make the server
// disagree with its own contract.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IngestResponse))]
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(CreateProjectResponse))]
[JsonSerializable(typeof(ProjectSettings))]
[JsonSerializable(typeof(SchemaSpec))]
[JsonSerializable(typeof(IReadOnlyList<SchemaResponse>))]
[JsonSerializable(typeof(PutSchemaResponse))]
[JsonSerializable(typeof(ReplayResponse))]
[JsonSerializable(typeof(QuarantinePageResponse))]
[JsonSerializable(typeof(FunnelQuery))]
[JsonSerializable(typeof(FunnelResult))]
[JsonSerializable(typeof(RetentionQuery))]
[JsonSerializable(typeof(RetentionResult))]
[JsonSerializable(typeof(SegmentQuery))]
[JsonSerializable(typeof(SegmentResult))]
[JsonSerializable(typeof(SessionsQuery))]
[JsonSerializable(typeof(SessionsResult))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
internal sealed partial class ApiJson : JsonSerializerContext;
