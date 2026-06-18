using Application.RetryStore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.RetryStore;

public sealed class PostgresRetryStore(
    IOptions<RetryStoreOptions> options,
    ILogger<PostgresRetryStore> logger) : IRetryStore
{
    private string ConnectionString => options.Value.ConnectionString;

    public async Task PersistAsync(RetryRecord record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO retry_records
                (id, event_id, event_type, topic, partition, "offset", message_key,
                 payload, headers, first_failure_time, last_failure_time,
                 retry_count, next_retry_at, status, last_error_message,
                 last_error_type, correlation_id)
            VALUES
                (@id, @eventId, @eventType, @topic, @partition, @offset, @messageKey,
                 @payload, @headers, @firstFailureTime, @lastFailureTime,
                 @retryCount, @nextRetryAt, @status, @lastErrorMessage,
                 @lastErrorType, @correlationId)
            ON CONFLICT (topic, partition, "offset") DO NOTHING
            """;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("eventId", (object?)record.EventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("eventType", record.EventType);
        cmd.Parameters.AddWithValue("topic", record.Topic);
        cmd.Parameters.AddWithValue("partition", record.Partition);
        cmd.Parameters.AddWithValue("offset", record.Offset);
        cmd.Parameters.AddWithValue("messageKey", (object?)record.MessageKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("payload", record.Payload);
        cmd.Parameters.AddWithValue("headers", (object?)record.Headers ?? DBNull.Value);
        cmd.Parameters.AddWithValue("firstFailureTime", record.FirstFailureTime);
        cmd.Parameters.AddWithValue("lastFailureTime", record.LastFailureTime);
        cmd.Parameters.AddWithValue("retryCount", record.RetryCount);
        cmd.Parameters.AddWithValue("nextRetryAt", record.NextRetryAt);
        cmd.Parameters.AddWithValue("status", record.Status.ToString());
        cmd.Parameters.AddWithValue("lastErrorMessage", (object?)record.LastErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lastErrorType", (object?)record.LastErrorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("correlationId", (object?)record.CorrelationId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogInformation(
            "Persisted retry record {RecordId} for event {EventType} (topic={Topic}, partition={Partition}, offset={Offset})",
            record.Id, record.EventType, record.Topic, record.Partition, record.Offset);
    }

    public async Task<IReadOnlyList<RetryRecord>> GetDueRecordsAsync(int batchSize, CancellationToken ct = default)
    {
        // Atomically claim a batch: the inner SELECT uses FOR UPDATE SKIP LOCKED so
        // concurrent replicas skip already-claimed rows, and the outer UPDATE sets
        // status = 'InProgress' in the same statement — no separate MarkInProgressAsync needed.
        const string sql = """
            WITH claimed AS (
                UPDATE retry_records
                SET status = 'InProgress'
                WHERE id IN (
                    SELECT id FROM retry_records
                    WHERE status = 'Pending' AND next_retry_at <= @now
                    ORDER BY next_retry_at
                    LIMIT @limit
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id, event_id, event_type, topic, partition, "offset", message_key,
                          payload, headers, first_failure_time, last_failure_time,
                          retry_count, next_retry_at, status, last_error_message,
                          last_error_type, correlation_id
            )
            SELECT * FROM claimed
            ORDER BY next_retry_at
            """;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("limit", batchSize);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var records = new List<RetryRecord>();

        while (await reader.ReadAsync(ct))
        {
            records.Add(MapRecord(reader));
        }

        return records;
    }

    public async Task MarkInProgressAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE retry_records SET status = 'InProgress' WHERE id = @id
            """;

        await ExecuteAsync(sql, id, ct);
    }

    public async Task MarkSucceededAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE retry_records SET status = 'Succeeded' WHERE id = @id
            """;

        await ExecuteAsync(sql, id, ct);

        logger.LogInformation("Retry record {RecordId} marked as Succeeded", id);
    }

    public async Task RescheduleAsync(
        Guid id, int newRetryCount, DateTime nextRetryAt,
        string? errorMessage, string? errorType, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE retry_records
            SET status = 'Pending',
                retry_count = @retryCount,
                next_retry_at = @nextRetryAt,
                last_failure_time = @now,
                last_error_message = @errorMessage,
                last_error_type = @errorType
            WHERE id = @id
            """;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("retryCount", newRetryCount);
        cmd.Parameters.AddWithValue("nextRetryAt", nextRetryAt);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("errorType", (object?)errorType ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogInformation(
            "Rescheduled retry record {RecordId} (attempt {RetryCount}, next at {NextRetryAt})",
            id, newRetryCount, nextRetryAt);
    }

    public async Task MarkDeadLetterAsync(
        Guid id, string? errorMessage, string? errorType, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE retry_records
            SET status = 'DeadLetter',
                last_failure_time = @now,
                last_error_message = @errorMessage,
                last_error_type = @errorType
            WHERE id = @id
            """;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("errorType", (object?)errorType ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogError(
            "Retry record {RecordId} moved to DeadLetter — manual replay required. Last error: {Error}",
            id, errorMessage);
    }

    private async Task ExecuteAsync(string sql, Guid id, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static RetryRecord MapRecord(NpgsqlDataReader reader)
    {
        return new RetryRecord
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            EventId = reader.IsDBNull(reader.GetOrdinal("event_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("event_id")),
            EventType = reader.GetString(reader.GetOrdinal("event_type")),
            Topic = reader.GetString(reader.GetOrdinal("topic")),
            Partition = reader.GetInt32(reader.GetOrdinal("partition")),
            Offset = reader.GetInt64(reader.GetOrdinal("offset")),
            MessageKey = reader.IsDBNull(reader.GetOrdinal("message_key"))
                ? null
                : reader.GetString(reader.GetOrdinal("message_key")),
            Payload = reader.GetString(reader.GetOrdinal("payload")),
            Headers = reader.IsDBNull(reader.GetOrdinal("headers"))
                ? null
                : reader.GetString(reader.GetOrdinal("headers")),
            FirstFailureTime = reader.GetDateTime(reader.GetOrdinal("first_failure_time")),
            LastFailureTime = reader.GetDateTime(reader.GetOrdinal("last_failure_time")),
            RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
            NextRetryAt = reader.GetDateTime(reader.GetOrdinal("next_retry_at")),
            Status = Enum.Parse<RetryRecordStatus>(reader.GetString(reader.GetOrdinal("status"))),
            LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("last_error_message"))
                ? null
                : reader.GetString(reader.GetOrdinal("last_error_message")),
            LastErrorType = reader.IsDBNull(reader.GetOrdinal("last_error_type"))
                ? null
                : reader.GetString(reader.GetOrdinal("last_error_type")),
            CorrelationId = reader.IsDBNull(reader.GetOrdinal("correlation_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("correlation_id")),
        };
    }
}
