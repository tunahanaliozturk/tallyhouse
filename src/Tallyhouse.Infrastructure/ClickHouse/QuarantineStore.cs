using System.Buffers.Text;
using System.Globalization;
using System.Text;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.ADO.Readers;
using ClickHouse.Driver.Utility;

namespace Tallyhouse.Infrastructure.ClickHouse;

public sealed record QuarantinedEvent(
    Guid QuarantineId,
    DateTimeOffset ReceivedAt,
    string MessageId,
    string EventName,
    string Reason,
    string Payload,
    bool Replayed);

public sealed record QuarantinePage(IReadOnlyList<QuarantinedEvent> Items, string? Next);

/// <summary>
/// Reads quarantine for triage and records replays. A replay is a new row with the same key and a newer
/// <c>updated_at</c>, which the ReplacingMergeTree keeps; reads use FINAL so a replayed event never shows as
/// open, even before the merge.
/// </summary>
public sealed class QuarantineStore(ClickHouseClient client, TimeProvider time)
{
    public const int MaxPageSize = 200;

    /// <summary>Open events, newest first. The cursor is opaque to callers and positions the next page exactly.</summary>
    public async Task<QuarantinePage> ListOpenAsync(Guid projectId, int limit, string? cursor, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("limit", (uint)(limit + 1));

        string after = string.Empty;

        if (TryDecode(cursor, out DateTime receivedAt, out Guid quarantineId))
        {
            parameters.AddParameter("cursorAt", receivedAt);
            parameters.AddParameter("cursorId", quarantineId);
            after = " AND (received_at, quarantine_id) < ({cursorAt:DateTime64(3, 'UTC')}, {cursorId:UUID})";
        }

        string sql = $$"""
            SELECT quarantine_id, received_at, message_id, event_name, reason, payload, status = 'replayed'
            FROM quarantine FINAL
            WHERE project_id = {project:UUID} AND status = 'open'{{after}}
            ORDER BY received_at DESC, quarantine_id DESC
            LIMIT {limit:UInt32}
            """;

        List<QuarantinedEvent> items = await ReadAsync(sql, parameters, cancellationToken);

        if (items.Count <= limit)
        {
            return new QuarantinePage(items, null);
        }

        items.RemoveAt(items.Count - 1);
        QuarantinedEvent last = items[^1];
        return new QuarantinePage(items, Encode(last.ReceivedAt.UtcDateTime, last.QuarantineId));
    }

    public async Task<QuarantinedEvent?> FindAsync(Guid projectId, Guid quarantineId, CancellationToken cancellationToken)
    {
        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("id", quarantineId);

        List<QuarantinedEvent> items = await ReadAsync(
            """
            SELECT quarantine_id, received_at, message_id, event_name, reason, payload, status = 'replayed'
            FROM quarantine FINAL
            WHERE project_id = {project:UUID} AND quarantine_id = {id:UUID}
            """,
            parameters,
            cancellationToken);

        return items.Count == 0 ? null : items[0];
    }

    public async Task MarkReplayedAsync(Guid projectId, QuarantinedEvent quarantined, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quarantined);

        ClickHouseParameterCollection parameters = new();
        parameters.AddParameter("project", projectId);
        parameters.AddParameter("receivedAt", quarantined.ReceivedAt.UtcDateTime);
        parameters.AddParameter("id", quarantined.QuarantineId);
        parameters.AddParameter("messageId", quarantined.MessageId);
        parameters.AddParameter("event", quarantined.EventName);
        parameters.AddParameter("reason", quarantined.Reason);
        parameters.AddParameter("payload", quarantined.Payload);
        parameters.AddParameter("updatedAt", time.GetUtcNow().UtcDateTime);

        await client.ExecuteNonQueryAsync(
            """
            INSERT INTO quarantine (project_id, received_at, quarantine_id, message_id, event_name, reason, payload, status, updated_at)
            VALUES ({project:UUID}, {receivedAt:DateTime64(3, 'UTC')}, {id:UUID}, {messageId:String}, {event:String},
                    {reason:String}, {payload:String}, 'replayed', {updatedAt:DateTime64(3, 'UTC')})
            """,
            parameters,
            cancellationToken: cancellationToken);
    }

    private async Task<List<QuarantinedEvent>> ReadAsync(string sql, ClickHouseParameterCollection parameters, CancellationToken cancellationToken)
    {
        List<QuarantinedEvent> items = [];

        using ClickHouseDataReader reader = await client.ExecuteReaderAsync(sql, parameters, cancellationToken: cancellationToken);

        while (reader.Read())
        {
            items.Add(new QuarantinedEvent(
                reader.GetGuid(0),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture)));
        }

        return items;
    }

    private static string Encode(DateTime receivedAt, Guid quarantineId) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{receivedAt.Ticks}:{quarantineId:N}")));

    private static bool TryDecode(string? cursor, out DateTime receivedAt, out Guid quarantineId)
    {
        receivedAt = default;
        quarantineId = default;

        if (string.IsNullOrEmpty(cursor) || cursor.Length > 128)
        {
            return false;
        }

        try
        {
            string[] parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');

            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                && ticks is > 0 and < 3_155_378_975_999_999_999
                && Guid.TryParseExact(parts[1], "N", out quarantineId))
            {
                receivedAt = new DateTime(ticks, DateTimeKind.Utc);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
