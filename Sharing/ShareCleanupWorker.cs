using CartoonArchive.Data;
using  Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CartoonArchive.Sharing;

public sealed class ShareCleanupWorker(IServiceProvider services, IOptions<ShareOptions> options, ILogger<ShareCleanupWorker> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(options.Value.SweepIntervalMinutes);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Share cleanup worker started (sweep every {Minutes} min)", _interval.TotalMinutes);

        while(!ct.IsCancellationRequested)
        {
            try 
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CartoonArchiveDbContext>();
                var now = DateTimeOffset.UtcNow;
                // Raw SQL — SQLite's EF provider can't translate
                // ExecuteDeleteAsync with a DateTimeOffset comparison.
                var removed = await db.Database.ExecuteSqlAsync(
                    $"DELETE FROM ShareTokens WHERE MetadataUntil < {now}",
                    ct
                );
                if (removed > 0)
                    logger.LogInformation("Removed {Count} expired share tokens", removed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Share cleanup sweep failed.");
            }

            try 
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Share cleanup worker stopped");
    }
}
