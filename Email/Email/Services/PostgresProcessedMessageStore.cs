using Npgsql;

namespace Email.Services;

public sealed class PostgresProcessedMessageStore(
    IConfiguration configuration,
    ILogger<PostgresProcessedMessageStore> logger) : IProcessedMessageStore
{
    private readonly string _connectionString =
        configuration.GetConnectionString("Postgres")
        ?? configuration["ConnectionStrings:Postgres"]
        ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=email_service";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS email_processed_messages (
                message_id uuid PRIMARY KEY,
                processed_at_utc timestamp without time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_email_processed_messages_processed_at
                ON email_processed_messages (processed_at_utc);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation("Ensured table email_processed_messages exists");
    }

    public async Task<bool> IsProcessedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT 1 FROM email_processed_messages WHERE message_id = @message_id LIMIT 1;";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("message_id", messageId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    public async Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO email_processed_messages (message_id, processed_at_utc)
            VALUES (@message_id, @processed_at_utc)
            ON CONFLICT (message_id) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("processed_at_utc", DateTime.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteOldEntriesAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM email_processed_messages
            WHERE processed_at_utc < @cutoff;
            """;

        var cutoff = DateTime.UtcNow - retention;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", cutoff);

        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation(
            "Cleaned up {Deleted} processed-message record(s) older than {Cutoff:u}",
            deleted, cutoff);
    }
}