using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LunchSuggest.Modules.Dishes.Internal;

/// <summary>Queues dish lookups and runs them one at a time: the local LLM is CPU-bound, so parallel runs only slow each other down.</summary>
internal sealed class DishGuide(
    IDbContextFactory<DishesDbContext> dbFactory,
    IServiceProvider services,
    DishExtractor extractor,
    ILogger<DishGuide> log) : BackgroundService, IDishGuide
{
    static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
    // Two lanes: urgent lookups (someone is waiting on a spinner for today's picks) go before background ones.
    readonly Channel<DishQuery> _urgent = Channel.CreateUnbounded<DishQuery>(), _normal = Channel.CreateUnbounded<DishQuery>();
    readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();

    // ponytail: in-process queue and event = one server instance; queued lookups are lost on restart (users just click again).
    public event Action<string>? LookupFinished;
    public event Action? SuggestionsChanged;

    public bool IsPending(string placeId) => _pending.ContainsKey(placeId);

    public void Request(DishQuery place) => Enqueue(place, urgent: false);

    public async Task<IReadOnlyList<string>> GetOrLookUpAsync(DishQuery place, CancellationToken ct = default)
    {
        if ((await GetKnownAsync([place.PlaceId], ct)).TryGetValue(place.PlaceId, out var known)) return known; // incl. suggested-only
        await Enqueue(place, urgent: true).WaitAsync(ct);
        return (await GetKnownAsync([place.PlaceId], ct)).GetValueOrDefault(place.PlaceId) ?? [];
    }

    Task Enqueue(DishQuery place, bool urgent)
    {
        var added = false;
        var tcs = _pending.GetOrAdd(place.PlaceId, _ => { added = true; return new(TaskCreationOptions.RunContinuationsAsynchronously); });
        // Already waiting in the normal lane and now urgent: queue it again up front; the worker skips the stale copy.
        if (added || urgent) (urgent ? _urgent : _normal).Writer.TryWrite(place);
        return tcs.Task;
    }

    Task<bool>? _waitUrgent, _waitNormal; // reused, so the idle lane doesn't pile up a waiter per lookup

    async Task<DishQuery> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_urgent.Reader.TryRead(out var u) || _normal.Reader.TryRead(out u)) return u;
            _waitUrgent = _waitUrgent is { IsCompleted: false } ? _waitUrgent : _urgent.Reader.WaitToReadAsync(ct).AsTask();
            _waitNormal = _waitNormal is { IsCompleted: false } ? _waitNormal : _normal.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(_waitUrgent, _waitNormal);
            ct.ThrowIfCancellationRequested();
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetKnownAsync(IReadOnlyCollection<string> placeIds, CancellationToken ct = default)
    {
        var found = await GetLookedUpAsync(placeIds, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var approved = await db.SuggestedDishes.AsNoTracking()
            .Where(d => d.Approved && placeIds.Contains(d.PlaceId)).OrderBy(d => d.SuggestedAt).ToListAsync(ct);
        var suggested = approved.Where(d => !d.Remove).ToLookup(d => d.PlaceId, d => d.Dish);
        var removed = approved.Where(d => d.Remove).ToLookup(d => d.PlaceId, d => d.Dish);
        return placeIds.Distinct()
            .Where(id => found.ContainsKey(id) || suggested.Contains(id))
            .ToDictionary(id => id, id => WithSuggested(suggested[id], found.GetValueOrDefault(id), removed[id]));
    }

    /// <summary>
    /// Suggested dishes first (people who eat there picked them), then the looked-up ones; each dish once, minus removed ones.
    /// </summary>
    internal static IReadOnlyList<string> WithSuggested(IEnumerable<string> suggested, IEnumerable<string>? found, IEnumerable<string>? removed = null)
    {
        var gone = (removed ?? []).Select(Key).ToHashSet();
        return suggested.Concat(found ?? []).DistinctBy(Key).Where(d => !gone.Contains(Key(d))).ToList();
    }

    static string Key(string dish) => dish.Trim().ToLowerInvariant();

    // Only the web lookup's cache: whether a place still needs looking up doesn't depend on suggestions.
    async Task<Dictionary<string, IReadOnlyList<string>>> GetLookedUpAsync(IReadOnlyCollection<string> placeIds, CancellationToken ct)
    {
        var fresh = DateTimeOffset.UtcNow - CacheFor;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PlaceDishes.AsNoTracking()
            .Where(d => placeIds.Contains(d.PlaceId) && d.LookedUpAt > fresh)
            // Drinks are dropped on read too, for lists cached before the food-only rule.
            .ToDictionaryAsync(d => d.PlaceId, d => (IReadOnlyList<string>)d.Dishes.Where(x => !DishExtractor.IsDrink(x)).ToList(), ct);
    }

    public async Task<bool> SuggestDishAsync(string placeId, string placeName, string dish, string suggestedBy, bool remove = false, CancellationToken ct = default)
    {
        var row = new SuggestedDish
        {
            Id = Guid.CreateVersion7(),
            PlaceId = Required(placeId, 200, nameof(placeId)),
            PlaceName = Required(placeName, SuggestedDish.MaxPlaceName, nameof(placeName)),
            Dish = Required(dish, SuggestedDish.MaxDish, nameof(dish)),
            SuggestedBy = Required(suggestedBy, SuggestedDish.MaxBy, nameof(suggestedBy)),
            SuggestedAt = DateTimeOffset.UtcNow,
            Remove = remove,
        };
        if (!remove && DishExtractor.IsDrink(row.Dish)) return false;
        var key = Key(row.Dish);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listed = ((await GetKnownAsync([row.PlaceId], ct)).GetValueOrDefault(row.PlaceId) ?? []).Any(d => Key(d) == key);
        var waiting = (await db.SuggestedDishes.Where(d => d.PlaceId == row.PlaceId && !d.Approved && d.Remove == remove)
            .Select(d => d.Dish).ToListAsync(ct)).Any(d => Key(d) == key);
        // Already asked for; or adding one that's listed, or removing one that isn't: nothing to do.
        if (waiting || listed != remove) return true;
        db.SuggestedDishes.Add(row);
        await db.SaveChangesAsync(ct);
        log.LogInformation(remove ? "{By} asked to remove {Dish} at {Place}" : "{By} suggested {Dish} at {Place}", row.SuggestedBy, row.Dish, row.PlaceName);
        SuggestionsChanged?.Invoke();
        return true;
    }

    static string Required(string? value, int max, string field)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) throw new ArgumentException($"{field} is required.");
        return value.Length <= max ? value : throw new ArgumentException($"{field} must be at most {max} characters.");
    }

    public async Task<IReadOnlyList<DishSuggestion>> GetSuggestedDishesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SuggestedDishes.AsNoTracking().OrderBy(d => d.Approved).ThenByDescending(d => d.SuggestedAt)
            .Select(d => new DishSuggestion(d.Id, d.PlaceId, d.PlaceName, d.Dish, d.SuggestedBy, d.SuggestedAt, d.Approved, d.Remove))
            .ToListAsync(ct);
    }

    public async Task<bool> ReviewDishAsync(Guid id, bool approve, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.SuggestedDishes.FindAsync([id], ct) is not { } row) return false;
        if (approve)
        {
            row.Approved = true;
            // The latest decision wins: an earlier approved add/removal of the same dish goes.
            var opposite = await db.SuggestedDishes.Where(d => d.PlaceId == row.PlaceId && d.Approved && d.Remove != row.Remove).ToListAsync(ct);
            db.SuggestedDishes.RemoveRange(opposite.Where(d => Key(d.Dish) == Key(row.Dish)));
        }
        else db.SuggestedDishes.Remove(row);
        await db.SaveChangesAsync(ct);
        LookupFinished?.Invoke(row.PlaceId); // open pages reload this place's dishes
        SuggestionsChanged?.Invoke();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DishQuery place;
            try { place = await NextAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            if (!_pending.ContainsKey(place.PlaceId)) continue; // stale duplicate, already done
            try
            {
                if ((await GetLookedUpAsync([place.PlaceId], stoppingToken)).Count == 0)
                    await LookUpAsync(place, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            // Not cached on failure, so the next request retries.
            catch (Exception ex) { log.LogWarning(ex, "Dish lookup failed for {Place}", place.Name); }
            finally
            {
                if (_pending.TryRemove(place.PlaceId, out var done)) done.TrySetResult();
                LookupFinished?.Invoke(place.PlaceId);
            }
        }
    }

    async Task LookUpAsync(DishQuery place, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        // Typed HttpClients are transient; resolve per lookup instead of holding one forever.
        var search = services.GetRequiredService<WebSearch>();
        var results = await search.SearchAsync($"{place.Name} {place.Address} menu", 8, ct);
        if (results.Count == 0)
        {
            // Usually upstream engines rate-limiting SearXNG: don't cache "no dishes" for a month, let the next request retry.
            log.LogWarning("Web search returned nothing for {Place}; not caching", place.Name);
            return;
        }
        var dishes = await extractor.ExtractAsync(place, results, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PlaceDishes.Where(d => d.PlaceId == place.PlaceId).ExecuteDeleteAsync(ct);
        db.PlaceDishes.Add(new PlaceDishes { PlaceId = place.PlaceId, Dishes = dishes, LookedUpAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Found {Count} dishes for {Place} from {Results} results in {Seconds:F0}s",
            dishes.Count, place.Name, results.Count, (DateTimeOffset.UtcNow - started).TotalSeconds);
    }
}
