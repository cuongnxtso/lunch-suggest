using System.Globalization;
using LunchSuggest.Modules.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LunchSuggest.Modules.Places.Internal;

internal sealed record PlacesOptions(TimeSpan CacheFor);

internal sealed class PlaceDirectory(
    IDbContextFactory<PlacesDbContext> dbFactory,
    IPlaceProvider provider,
    ISemanticIndex index,
    PlacesOptions options,
    ILogger<PlaceDirectory> log) : IPlaceDirectory
{
    // Sparse map data (e.g. OSM outside Europe): widen the search until there's enough choice.
    internal const int MinPlaces = 20, MaxRadiusMeters = 2000;
    readonly SemaphoreSlim _fetchLock = new(1, 1);

    public event Action? SuggestionsChanged;

    public async Task<IReadOnlyList<PlaceInfo>> GetNearbyAsync(double lat, double lng, int radiusMeters, CancellationToken ct = default)
    {
        var radius = radiusMeters;
        var result = await LoadAsync(lat, lng, radius, ct);
        while (result.Count < MinPlaces && radius < MaxRadiusMeters)
        {
            radius = Math.Min(radius * 2, MaxRadiusMeters);
            result = await LoadAsync(lat, lng, radius, ct);
        }
        if (radius > radiusMeters) // keep the nearest few beyond the asked radius, not the whole widened area
            result = result.Where((p, i) => p.DistanceMeters <= radiusMeters || i < MinPlaces).ToList();

        // Embedding is slow on CPU, so don't make the page wait. Already-indexed places are skipped, so this self-heals if Ollama was down.
        var docs = result.Select(p => new SearchDocument(p.Id, DescribeForSearch(p), p.Lat, p.Lng)).ToList();
        _ = Task.Run(async () =>
        {
            try { await index.IndexAsync(docs); }
            catch (Exception ex) { log.LogWarning(ex, "Semantic indexing failed for {Count} places", docs.Count); }
        });
        return result;
    }

    async Task<List<PlaceInfo>> LoadAsync(double lat, double lng, int radiusMeters, CancellationToken ct)
    {
        // ~110 m grid: people in the same office share one fetch.
        // Provider in the key and the filter: switching provider must not serve (or mix in) the old one's data.
        var key = string.Create(CultureInfo.InvariantCulture, $"{provider.Source}:{Math.Round(lat, 3)}:{Math.Round(lng, 3)}:{radiusMeters}");
        await EnsureFetchedAsync(key, lat, lng, radiusMeters, ct);

        var dLat = radiusMeters / 111_320d;
        var dLng = radiusMeters / (111_320d * Math.Cos(lat * Math.PI / 180));
        var idPrefix = provider.Source + ":";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Places.AsNoTracking()
            .Where(p => p.Id.StartsWith(idPrefix))
            .Where(p => p.Lat >= lat - dLat && p.Lat <= lat + dLat && p.Lng >= lng - dLng && p.Lng <= lng + dLng)
            .ToListAsync(ct);

        // Approved suggestions join whatever provider is in use.
        var suggested = await db.SuggestedPlaces.AsNoTracking()
            .Where(p => p.Approved && p.Lat >= lat - dLat && p.Lat <= lat + dLat && p.Lng >= lng - dLng && p.Lng <= lng + dLng)
            .Select(p => new Place { Id = p.Id, Name = p.Name, Cuisine = p.Cuisine, Category = SuggestedCategory, Address = p.Address, Lat = p.Lat, Lng = p.Lng })
            .ToListAsync(ct);

        var removed = (await db.RemovedPlaces.AsNoTracking().Where(r => r.Approved).Select(r => r.PlaceId).ToListAsync(ct)).ToHashSet();

        return rows.Concat(suggested)
            .Where(p => !removed.Contains(p.Id))
            .Select(p => new PlaceInfo(p.Id, p.Name, p.Cuisine, p.Category, p.Address, p.Lat, p.Lng, Geo.DistanceMeters(lat, lng, p.Lat, p.Lng)))
            .Where(p => p.DistanceMeters <= radiusMeters)
            .Where(p => !FoodCategory.IsDrinkOnly(p.Category)) // food only; at read time so cached rows are filtered too
            .OrderBy(p => p.DistanceMeters)
            .ToList();
    }

    internal const string SuggestedCategory = "restaurant";

    public async Task<string> SuggestPlaceAsync(string name, string? cuisine, string? address, double lat, double lng, string suggestedBy, CancellationToken ct = default)
    {
        var place = Validated(name, cuisine, address, lat, lng, suggestedBy);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.SuggestedPlaces.Add(place);
        await db.SaveChangesAsync(ct);
        log.LogInformation("{By} suggested the place {Name}", place.SuggestedBy, place.Name);
        SuggestionsChanged?.Invoke();
        return place.Id;
    }

    internal static SuggestedPlace Validated(string name, string? cuisine, string? address, double lat, double lng, string suggestedBy)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lng) || Math.Abs(lat) > 90 || Math.Abs(lng) > 180)
            throw new ArgumentException("The location is not a valid point.");
        return new SuggestedPlace
        {
            Id = PlacesModule.SuggestedIdPrefix + Guid.CreateVersion7().ToString("N"),
            Name = Text(name, SuggestedPlace.MaxName, nameof(name)) ?? throw new ArgumentException("name is required."),
            Cuisine = Text(cuisine, SuggestedPlace.MaxCuisine, nameof(cuisine)),
            Address = Text(address, SuggestedPlace.MaxAddress, nameof(address)),
            Lat = lat, Lng = lng,
            SuggestedBy = Text(suggestedBy, SuggestedPlace.MaxBy, nameof(suggestedBy)) ?? throw new ArgumentException("suggestedBy is required."),
            SuggestedAt = DateTimeOffset.UtcNow,
        };
    }

    // Trimmed; blank = null.
    static string? Text(string? value, int max, string field)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : throw new ArgumentException($"{field} must be at most {max} characters.");
    }

    public async Task<IReadOnlyList<PlaceSuggestion>> GetSuggestedPlacesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SuggestedPlaces.AsNoTracking().OrderBy(p => p.Approved).ThenByDescending(p => p.SuggestedAt)
            .Select(p => new PlaceSuggestion(p.Id, p.Name, p.Cuisine, p.Address, p.Lat, p.Lng, p.SuggestedBy, p.SuggestedAt, p.Approved))
            .ToListAsync(ct);
    }

    public async Task<bool> ReviewPlaceAsync(string id, bool approve, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = approve
            ? await db.SuggestedPlaces.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Approved, true), ct) > 0
            : await db.SuggestedPlaces.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0;
        if (changed) SuggestionsChanged?.Invoke();
        return changed;
    }

    public async Task SuggestRemovalAsync(PlaceInfo place, string suggestedBy, CancellationToken ct = default)
    {
        var by = Text(suggestedBy, SuggestedPlace.MaxBy, nameof(suggestedBy)) ?? throw new ArgumentException("suggestedBy is required.");
        var id = Text(place.Id, 200, "place id") ?? throw new ArgumentException("place id is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.RemovedPlaces.AnyAsync(r => r.PlaceId == id, ct)) return; // already asked for (or already removed)
        db.RemovedPlaces.Add(new RemovedPlace
        {
            PlaceId = id, Name = place.Name.Length <= 200 ? place.Name : place.Name[..200], Lat = place.Lat, Lng = place.Lng,
            SuggestedBy = by, SuggestedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("{By} asked to remove the place {Name}", by, place.Name);
        SuggestionsChanged?.Invoke();
    }

    public async Task<IReadOnlyList<PlaceRemoval>> GetRemovalsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RemovedPlaces.AsNoTracking().OrderBy(r => r.Approved).ThenByDescending(r => r.SuggestedAt)
            .Select(r => new PlaceRemoval(r.PlaceId, r.Name, r.Lat, r.Lng, r.SuggestedBy, r.SuggestedAt, r.Approved))
            .ToListAsync(ct);
    }

    public async Task<bool> ReviewRemovalAsync(string placeId, bool approve, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = approve
            ? await db.RemovedPlaces.Where(r => r.PlaceId == placeId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Approved, true), ct) > 0
            : await db.RemovedPlaces.Where(r => r.PlaceId == placeId).ExecuteDeleteAsync(ct) > 0;
        if (changed) SuggestionsChanged?.Invoke();
        return changed;
    }

    internal static string DescribeForSearch(PlaceInfo p) =>
        string.Join(". ", new[] { p.Name, p.Category, p.Cuisine is null ? null : $"Cuisine: {p.Cuisine}",
                DishGlossary.Describe(p.Name) is { } dishes ? $"Serves: {dishes}" : null, p.Address }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    async Task EnsureFetchedAsync(string key, double lat, double lng, int radiusMeters, CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var fetched = await db.AreaFetches.FindAsync([key], ct);
            if (fetched is not null && fetched.FetchedAt > DateTimeOffset.UtcNow - options.CacheFor) return;

            var places = (await provider.FetchAsync(lat, lng, radiusMeters, ct)).DistinctBy(p => p.Id).ToList();
            var ids = places.Select(p => p.Id).ToList();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Places.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct);
            db.Places.AddRange(places);
            if (fetched is null) db.AreaFetches.Add(new AreaFetch { Key = key, FetchedAt = DateTimeOffset.UtcNow });
            else fetched.FetchedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            log.LogInformation("Fetched {Count} places for area {Key}", places.Count, key);
        }
        finally { _fetchLock.Release(); }
    }
}

internal static class Geo
{
    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6_371_000, rad = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * rad / 2), 2)
              + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Pow(Math.Sin((lng2 - lng1) * rad / 2), 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}
