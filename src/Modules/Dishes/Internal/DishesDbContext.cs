using Microsoft.EntityFrameworkCore;

namespace LunchSuggest.Modules.Dishes.Internal;

internal sealed class PlaceDishes
{
    public required string PlaceId { get; set; }   // id from the Places module; no FK across modules
    public List<string> Dishes { get; set; } = [];  // empty = looked up, nothing found
    public DateTimeOffset LookedUpAt { get; set; }
}

/// <summary>A dish suggested by a voter for a place. Kept apart from <see cref="PlaceDishes"/>, a lookup cache that expires.</summary>
internal sealed class SuggestedDish
{
    public const int MaxDish = 120, MaxPlaceName = 120, MaxBy = 40;
    public Guid Id { get; set; }
    public required string PlaceId { get; set; }   // id from the Places module; no FK across modules
    public required string PlaceName { get; set; } // for the Settings page, which lists suggestions without loading places
    public required string Dish { get; set; }
    public required string SuggestedBy { get; set; }
    public DateTimeOffset SuggestedAt { get; set; }
    public bool Approved { get; set; }
    public bool Remove { get; set; }               // a request to take the dish off the place's list
}

internal sealed class DishesDbContext(DbContextOptions<DishesDbContext> options) : DbContext(options)
{
    public const string Schema = "dishes";
    public DbSet<PlaceDishes> PlaceDishes => Set<PlaceDishes>();
    public DbSet<SuggestedDish> SuggestedDishes => Set<SuggestedDish>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<PlaceDishes>(e => { e.HasKey(x => x.PlaceId); e.Property(x => x.PlaceId).HasMaxLength(200); });
        b.Entity<SuggestedDish>(e =>
        {
            e.Property(x => x.PlaceId).HasMaxLength(200);
            e.Property(x => x.PlaceName).HasMaxLength(SuggestedDish.MaxPlaceName);
            e.Property(x => x.Dish).HasMaxLength(SuggestedDish.MaxDish);
            e.Property(x => x.SuggestedBy).HasMaxLength(SuggestedDish.MaxBy);
            e.HasIndex(x => x.PlaceId);
        });
    }
}
