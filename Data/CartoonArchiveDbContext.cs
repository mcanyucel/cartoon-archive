using CartoonArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace CartoonArchive.Data;

public sealed class CartoonArchiveDbContext(DbContextOptions<CartoonArchiveDbContext> options)
    : DbContext(options)
{
    public DbSet<Cartoon> Cartoons => Set<Cartoon>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var cartoon = modelBuilder.Entity<Cartoon>();

        cartoon.HasIndex(c => c.Sha256).IsUnique();
        cartoon.HasIndex(c => c.Status);

        cartoon.Property(c => c.Sha256).HasMaxLength(64);
        cartoon.Property(c => c.Language).HasMaxLength(16);
        cartoon.Property(c => c.ContentType).HasMaxLength(64);
    }
}
