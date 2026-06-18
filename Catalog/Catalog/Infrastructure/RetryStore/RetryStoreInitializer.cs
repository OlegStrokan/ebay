using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.RetryStore;

public sealed class RetryStoreInitializer(
    IOptions<RetryStoreOptions> options,
    ILogger<RetryStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS retry_records (
                id              UUID PRIMARY KEY,
                event_id        UUID,
                event_type      TEXT NOT NULL,
                topic           TEXT NOT NULL,
                partition       INT NOT NULL,
                "offset"        BIGINT NOT NULL,
                message_key     TEXT,
                payload         TEXT NOT NULL,
                headers         TEXT,
                first_failure_time TIMESTAMPTZ NOT NULL,
                last_failure_time  TIMESTAMPTZ NOT NULL,
                retry_count     INT NOT NULL DEFAULT 0,
                next_retry_at   TIMESTAMPTZ NOT NULL,
                status          TEXT NOT NULL DEFAULT 'Pending',
                last_error_message TEXT,
                last_error_type    TEXT,
                correlation_id     TEXT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS uq_retry_records_topic_partition_offset
                ON retry_records (topic, partition, "offset");

            CREATE INDEX IF NOT EXISTS idx_retry_records_pending_due
                ON retry_records (next_retry_at)
                WHERE status = 'Pending';
            """;

        try
        {
            await using var conn = new NpgsqlConnection(options.Value.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            logger.LogInformation("Retry store table ensured");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialise retry store table — service will start but retry persistence may fail");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
