using CartoonArchive.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CartoonArchive.Tagging;

public sealed class TagCleanupWorker(
    IServiceProvider services,
    IOptions<TagOptions> options,
    ILogger<TagCleanupWorker> logger
) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(options.Value.SweepIntervalMinutes);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "Tag cleanup worker started (sweep every {Minutes} min)",
            _interval.TotalMinutes
        );

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CartoonArchiveDbContext>();
                var removed = await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM Tags WHERE Id NOT IN (SELECT DISTINCT TagId FROM CartoonTags)",
                    ct
                );
                if (removed > 0)
                    logger.LogInformation("Removed {Count} orphan tags", removed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tag cleanup sweep failed.");
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

        logger.LogInformation("Tag cleanup worker stopped");
    }
}
