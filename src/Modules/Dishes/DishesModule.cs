using LunchSuggest.Modules.Dishes.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace LunchSuggest.Modules.Dishes;

/// <summary>What the Dishes module needs to know about a place to look up its dishes.</summary>
public sealed record DishQuery(string PlaceId, string Name, string? Category, string? Address);

/// <summary>Public contract of the Dishes module: top dishes per place, found by web search + a local LLM.</summary>
public interface IDishGuide
{
    /// <summary>Raised with the place id when a lookup finishes (with or without dishes).</summary>
    event Action<string>? LookupFinished;

    /// <summary>Cached dishes for these places; places never looked up (or expired) are absent.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetKnownAsync(IReadOnlyCollection<string> placeIds, CancellationToken ct = default);

    /// <summary>Queue a background lookup. No-op if one is already queued or running for this place.</summary>
    void Request(DishQuery place);

    /// <summary>Cached dishes, or an urgent lookup (ahead of background ones) awaited until done. Empty if nothing was found.</summary>
    Task<IReadOnlyList<string>> GetOrLookUpAsync(DishQuery place, CancellationToken ct = default);

    bool IsPending(string placeId);
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
