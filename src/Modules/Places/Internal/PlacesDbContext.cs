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

/// <summary>A place suggested by a voter. Kept apart from <see cref="Place"/>, which is a provider cache that gets refreshed.</summary>
internal sealed class SuggestedPlace
{
    public const int MaxName = 120, MaxCuisine = 60, MaxAddress = 200, MaxBy = 40;
    public required string Id { get; set; }          // "user:<guid>"
    public required string Name { get; set; }
    public string? Cuisine { get; set; }
    public string? Address { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public required string SuggestedBy { get; set; }
    public DateTimeOffset SuggestedAt { get; set; }
    public bool Approved { get; set; }               // only approved ones are listed
}

/// <summary>A request to hide a place (any provider's, or a suggested one). Approved ones are left out of the nearby list.</summary>
internal sealed class RemovedPlace
{
    public required string PlaceId { get; set; }     // the key: one request per place
    public required string Name { get; set; }        // for the Settings page
    public double Lat { get; set; }
    public double Lng { get; set; }
    public required string SuggestedBy { get; set; }
    public DateTimeOffset SuggestedAt { get; set; }
    public bool Approved { get; set; }
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
    public DbSet<SuggestedPlace> SuggestedPlaces => Set<SuggestedPlace>();
    public DbSet<RemovedPlace> RemovedPlaces => Set<RemovedPlace>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<Place>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.Lat, x.Lng }); });
        b.Entity<AreaFetch>().HasKey(x => x.Key);
        b.Entity<SuggestedPlace>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(SuggestedPlace.MaxName);
            e.Property(x => x.Cuisine).HasMaxLength(SuggestedPlace.MaxCuisine);
            e.Property(x => x.Address).HasMaxLength(SuggestedPlace.MaxAddress);
            e.Property(x => x.SuggestedBy).HasMaxLength(SuggestedPlace.MaxBy);
        });
        b.Entity<RemovedPlace>(e =>
        {
            e.HasKey(x => x.PlaceId);
            e.Property(x => x.PlaceId).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.SuggestedBy).HasMaxLength(SuggestedPlace.MaxBy);
        });
    }
}
