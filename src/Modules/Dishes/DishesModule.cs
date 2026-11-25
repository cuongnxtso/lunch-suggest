using LunchSuggest.Modules.Dishes.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace LunchSuggest.Modules.Dishes;

/// <summary>What the Dishes module needs to know about a place to look up its dishes.</summary>
public sealed record DishQuery(string PlaceId, string Name, string? Category, string? Address);

/// <summary>A dish someone suggested for a place. Shown first in that place's dishes once approved on the Settings page.</summary>
/// <param name="Remove">A request to take the dish off the place's list instead (hidden once approved).</param>
public sealed record DishSuggestion(Guid Id, string PlaceId, string PlaceName, string Dish, string SuggestedBy,
    DateTimeOffset SuggestedAt, bool Approved, bool Remove = false);

/// <summary>Public contract of the Dishes module: top dishes per place, found by web search + a local LLM, plus suggested ones.</summary>
public interface IDishGuide
{
    /// <summary>Raised with the place id when a lookup finishes (with or without dishes), or a suggested dish was approved.</summary>
    event Action<string>? LookupFinished;

    /// <summary>Raised when a dish is suggested, approved or deleted (the review list refreshes).</summary>
    event Action? SuggestionsChanged;

    /// <summary>
    /// Known dishes for these places: approved suggestions first, then the cached lookup, minus approved removals.
    /// Places with neither (never looked up, or expired) are absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetKnownAsync(IReadOnlyCollection<string> placeIds, CancellationToken ct = default);

    /// <summary>Queue a background lookup. No-op if one is already queued or running for this place.</summary>
    void Request(DishQuery place);

    /// <summary>Cached dishes, or an urgent lookup (ahead of background ones) awaited until done. Empty if nothing was found.</summary>
    Task<IReadOnlyList<string>> GetOrLookUpAsync(DishQuery place, CancellationToken ct = default);

    bool IsPending(string placeId);

    /// <summary>
    /// Saves a suggested dish, waiting for approval. False if it's a drink (food only). A dish the place already has, or that's
    /// already waiting, isn't added twice. With <paramref name="remove"/>, asks for a listed dish to be taken off instead
    /// (no-op if it isn't listed or is already waiting). Bad input: <see cref="ArgumentException"/>.
    /// </summary>
    Task<bool> SuggestDishAsync(string placeId, string placeName, string dish, string suggestedBy, bool remove = false, CancellationToken ct = default);

    /// <summary>Every suggested dish: waiting ones first, then newest first.</summary>
    Task<IReadOnlyList<DishSuggestion>> GetSuggestedDishesAsync(CancellationToken ct = default);

    /// <summary>
    /// Approves a suggested dish (or removal), or deletes it (rejecting it, or undoing an approved one). The latest approved
    /// decision wins: approving an add drops an approved removal of the same dish, and the other way round. False if there's no such row.
    /// </summary>
    Task<bool> ReviewDishAsync(Guid id, bool approve, CancellationToken ct = default);
}

public static class DishesModule
{
    public static IServiceCollection AddDishesModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContextFactory<DishesDbContext>(o => o.UseNpgsql(config.GetConnectionString("Lunch"),
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DishesDbContext.Schema)));

        services.AddHttpClient<WebSearch>(h =>
        {
            h.BaseAddress = new Uri(config["Dishes:SearxngUrl"] ?? "http://localhost:8888");
            h.Timeout = TimeSpan.FromSeconds(30);
        });
        // Any Microsoft.Extensions.AI chat client works here; a small local model keeps it free.
        services.AddKeyedSingleton<IChatClient>(DishExtractor.ClientKey, new OllamaApiClient(
            new Uri(config["Dishes:OllamaUrl"] ?? config["Search:OllamaUrl"] ?? "http://localhost:11434"),
            config["Dishes:ChatModel"] ?? "qwen2.5:3b"));
        services.AddSingleton<DishExtractor>();
        services.AddSingleton<DishGuide>();
        services.AddSingleton<IDishGuide>(sp => sp.GetRequiredService<DishGuide>());
        services.AddHostedService(sp => sp.GetRequiredService<DishGuide>());
        return services;
    }

    public static async Task MigrateDishesModuleAsync(this IServiceProvider sp)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<DishesDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}
