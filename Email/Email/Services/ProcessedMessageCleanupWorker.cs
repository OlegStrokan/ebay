namespace Email.Services;

public sealed class ProcessedMessageCleanupWorker(
    IProcessedMessageStore store,
    IConfiguration configuration,
    ILogger<ProcessedMessageCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = configuration.GetValue("ProcessedMessage:RetentionDays", 30);
        var retention = TimeSpan.FromDays(retentionDays);

        logger.LogInformation(
            "Processed-message cleanup worker started. RetentionDays={RetentionDays} Interval={Interval}",
            retentionDays, CleanupInterval);

        // Small startup delay so the main consumer can initialise the table first
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.DeleteOldEntriesAsync(retention, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Processed-message cleanup failed; will retry in {Interval}", CleanupInterval);
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }
}
