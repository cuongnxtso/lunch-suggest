using Microsoft.EntityFrameworkCore;

namespace LunchSuggest.Modules.Dishes.Internal;

internal sealed class PlaceDishes
{
    public required string PlaceId { get; set; }   // id from the Places module; no FK across modules
    public List<string> Dishes { get; set; } = [];  // empty = looked up, nothing found
    public DateTimeOffset LookedUpAt { get; set; }
}

internal sealed class DishesDbContext(DbContextOptions<DishesDbContext> options) : DbContext(options)
{
    public const string Schema = "dishes";
    public DbSet<PlaceDishes> PlaceDishes => Set<PlaceDishes>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<PlaceDishes>(e => { e.HasKey(x => x.PlaceId); e.Property(x => x.PlaceId).HasMaxLength(200); });
    }
}
