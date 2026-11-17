using LunchSuggest.Modules.Search.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace LunchSuggest.Modules.Search;

/// <summary>A piece of text to make searchable by meaning, pinned to a location.</summary>
public sealed record SearchDocument(string Id, string Text, double Lat, double Lng);

/// <summary>Score is cosine similarity (1 = identical meaning).</summary>
public sealed record SearchHit(string Id, double Score);

/// <summary>Public contract of the Search module. Everything else in this project is internal.</summary>
public interface ISemanticIndex
{
    Task IndexAsync(IReadOnlyCollection<SearchDocument> docs, CancellationToken ct = default);
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, double lat, double lng, int radiusMeters, int top = 20, CancellationToken ct = default);
}

public static class SearchModule
{
    public static IServiceCollection AddSearchModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContextFactory<SearchDbContext>(o => o.UseNpgsql(config.GetConnectionString("Lunch"), n =>
        {
            n.UseVector();
            n.MigrationsHistoryTable("__EFMigrationsHistory", SearchDbContext.Schema);
        }));
        // Any Microsoft.Extensions.AI embedding generator works here (OpenAI, Azure OpenAI, ONNX...); Ollama keeps it free and local.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new OllamaApiClient(
            new Uri(config["Search:OllamaUrl"] ?? "http://localhost:11434"),
            config["Search:EmbeddingModel"] ?? "nomic-embed-text"));
        services.AddSingleton<ISemanticIndex, SemanticIndex>();
        return services;
    }

    public static async Task MigrateSearchModuleAsync(this IServiceProvider sp)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<SearchDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}
