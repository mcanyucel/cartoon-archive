using CartoonArchive.Models;
using Microsoft.EntityFrameworkCore;

namespace CartoonArchive.Data;

public sealed class CartoonArchiveDbContext(DbContextOptions<CartoonArchiveDbContext> options)
    : DbContext(options)
{
    public DbSet<Cartoon> Cartoons => Set<Cartoon>();
    public DbSet<ShareToken> ShareTokens => Set<ShareToken>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CartoonTag> CartoonTags => Set<CartoonTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var cartoon = modelBuilder.Entity<Cartoon>();

        cartoon.HasIndex(c => c.Sha256).IsUnique();
        cartoon.HasIndex(c => c.Status);

        cartoon.Property(c => c.Sha256).HasMaxLength(64);
        cartoon.Property(c => c.Language).HasMaxLength(16);
        cartoon.Property(c => c.ContentType).HasMaxLength(64);

        var tag = modelBuilder.Entity<Tag>();
        tag.Property(t => t.Name).HasMaxLength(64).IsRequired();
        tag.HasIndex(t => t.Name).IsUnique();

        var cartoonTag = modelBuilder.Entity<CartoonTag>();
        cartoonTag.HasKey(ct => new { ct.CartoonId, ct.TagId });
        cartoonTag.HasIndex(ct => ct.TagId);
        cartoonTag
            .HasOne<Cartoon>()
            .WithMany()
            .HasForeignKey(ct => ct.CartoonId)
            .OnDelete(DeleteBehavior.Cascade);
        cartoonTag
            .HasOne<Tag>()
            .WithMany()
            .HasForeignKey(ct => ct.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        var share = modelBuilder.Entity<ShareToken>();
        share.HasKey(s => s.Token);
        share.Property(s => s.Token).HasMaxLength(64);
        share.HasIndex(s => s.ExpiresAt);
        share
            .HasOne(s => s.Cartoon)
            .WithMany()
            .HasForeignKey(s => s.CartoonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
