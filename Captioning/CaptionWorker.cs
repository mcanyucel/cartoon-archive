using CartoonArchive.Data;
using CartoonArchive.Models;
using CartoonArchive.Storage;
using Microsoft.EntityFrameworkCore;

namespace CartoonArchive.Captioning;

public sealed class CaptionWorker(IServiceProvider services, ILogger<CaptionWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Caption worker started");

        while (!ct.IsCancellationRequested)
        {
            bool processed = false;

            try
            {
                processed = await ProcessNextAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Caption worker loop error");
            }

            if (!processed)
            {
                try
                {
                    await Task.Delay(IdleDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Caption worker stopped");
    }

    private async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CartoonArchiveDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<ImageStorage>();
        var captions = scope.ServiceProvider.GetRequiredService<ICaptionService>();

        var pending = await db
            .Cartoons.Where(c => c.Status == CartoonStatus.Pending)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (pending is null)
            return false;

        try
        {
            var imagePath = storage.GetPath(pending.Sha256, pending.ContentType);
            var caption = await captions.CaptionAsync(
                imagePath,
                pending.ContentType,
                pending.Language,
                ct
            );

            pending.Description = caption;
            pending.Status = CartoonStatus.Ready;
            pending.CaptionedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Captioned cartoon {Id}", pending.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to caption cartoon {Id}", pending.Id);
            pending.Status = CartoonStatus.Failed;
            pending.ErrorMessage = ex.Message;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}
