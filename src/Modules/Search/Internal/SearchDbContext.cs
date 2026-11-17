using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace LunchSuggest.Modules.Search.Internal;

internal sealed class DocumentEmbedding
{
    public required string Id { get; set; }
    public required string Text { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public required Vector Embedding { get; set; }
}

internal sealed class SearchDbContext(DbContextOptions<SearchDbContext> options) : DbContext(options)
{
    public const string Schema = "search";
    // ponytail: matches nomic-embed-text. Switching to a model with another size needs a new migration + reindex.
    public const int Dimensions = 768;

    public DbSet<DocumentEmbedding> Documents => Set<DocumentEmbedding>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");
        b.HasDefaultSchema(Schema);
        b.Entity<DocumentEmbedding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Embedding).HasColumnType($"vector({Dimensions})");
            e.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            e.HasIndex(x => new { x.Lat, x.Lng });
        });
    }
}
