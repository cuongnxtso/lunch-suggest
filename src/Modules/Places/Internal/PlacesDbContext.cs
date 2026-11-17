using Microsoft.EntityFrameworkCore;

namespace LunchSuggest.Modules.Places.Internal;

internal sealed class Place
{
    public required string Id { get; set; }          // "osm:node/123" or "google:ChIJ..."
    public required string Name { get; set; }
    public string? Cuisine { get; set; }
    public required string Category { get; set; }
    public string? Address { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Remembers which areas were fetched, so we don't hit the provider on every page load.</summary>
internal sealed class AreaFetch
{
    public required string Key { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

internal sealed class PlacesDbContext(DbContextOptions<PlacesDbContext> options) : DbContext(options)
{
    public const string Schema = "places";
    public DbSet<Place> Places => Set<Place>();
    public DbSet<AreaFetch> AreaFetches => Set<AreaFetch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Place>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.Lat, x.Lng }); });
        b.Entity<AreaFetch>().HasKey(x => x.Key);
    }
}
