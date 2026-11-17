using System.Text.Json;

namespace LunchSuggest.Modules.Dishes.Internal;

internal sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>Self-hosted SearXNG (meta search over Google, Bing, DuckDuckGo…), JSON API. Free, no key.</summary>
internal sealed class WebSearch(HttpClient http)
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int max, CancellationToken ct)
    {
        using var res = await http.GetAsync($"search?format=json&q={Uri.EscapeDataString(query)}", ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(doc.RootElement, max);
    }

    internal static List<SearchResult> Parse(JsonElement root, int max) =>
        root.TryGetProperty("results", out var results)
            ? results.EnumerateArray()
                .Select(r => new SearchResult(Str(r, "title"), Str(r, "url"), Str(r, "content")))
                .Where(r => r.Title.Length > 0 || r.Snippet.Length > 0)
                .Take(max)
                .ToList()
            : [];

    static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
