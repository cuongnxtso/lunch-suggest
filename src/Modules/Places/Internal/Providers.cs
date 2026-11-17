using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace LunchSuggest.Modules.Places.Internal;

internal interface IPlaceProvider
{
    /// <summary>Prefix of every place id this provider returns ("osm", "overture", "google").</summary>
    string Source { get; }
    Task<IReadOnlyList<Place>> FetchAsync(double lat, double lng, int radiusMeters, CancellationToken ct);
}

/// <summary>Free, keyless OpenStreetMap data via the public Overpass API.</summary>
internal sealed class OverpassPlaceProvider(HttpClient http) : IPlaceProvider
{
    public string Source => "osm";

    public async Task<IReadOnlyList<Place>> FetchAsync(double lat, double lng, int radiusMeters, CancellationToken ct)
    {
        var query = string.Create(CultureInfo.InvariantCulture,
            $"""[out:json][timeout:25];nwr["amenity"~"^(restaurant|fast_food|cafe|food_court)$"](around:{radiusMeters},{lat},{lng});out center tags;""");
        // The public server answers 429/502/504 when busy and asks clients to back off exponentially.
        for (var attempt = 1; ; attempt++)
        {
            using var res = await http.PostAsync("", new FormUrlEncodedContent([new("data", query)]), ct);
            if ((int)res.StatusCode is 429 or 502 or 503 or 504 && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); // 4s, 8s, 16s
                continue;
            }
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return Parse(doc.RootElement);
        }
    }

    internal static List<Place> Parse(JsonElement root)
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<Place>();
        foreach (var el in root.GetProperty("elements").EnumerateArray())
        {
            if (!el.TryGetProperty("tags", out var tags) || Tag(tags, "name") is not { } name) continue;
            var point = el.TryGetProperty("center", out var c) ? c : el; // ways/relations carry a center
            if (!point.TryGetProperty("lat", out var la) || !point.TryGetProperty("lon", out var lo)) continue;
            var address = string.Join(" ", new[] { Tag(tags, "addr:housenumber"), Tag(tags, "addr:street") }.Where(s => s is not null));
            list.Add(new Place
            {
                Id = $"osm:{el.GetProperty("type").GetString()}/{el.GetProperty("id").GetInt64()}",
                Name = name,
                Cuisine = Tag(tags, "cuisine")?.Replace(";", ", ").Replace('_', ' '),
                Category = (Tag(tags, "amenity") ?? "restaurant").Replace('_', ' '),
                Address = address.Length > 0 ? address : null,
                Lat = la.GetDouble(), Lng = lo.GetDouble(), UpdatedAt = now,
            });
        }
        return list;
    }

    static string? Tag(JsonElement tags, string key) => tags.TryGetProperty(key, out var v) ? v.GetString() : null;
}

/// <summary>
/// Google Places API (New) Nearby Search. Needs an API key on a billing-enabled project
/// (Pro SKU: 5,000 free calls/month, then billed). Returns at most 20 places per call.
/// </summary>
internal sealed class GooglePlacesProvider(HttpClient http) : IPlaceProvider
{
    public string Source => "google";

    public async Task<IReadOnlyList<Place>> FetchAsync(double lat, double lng, int radiusMeters, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchNearby")
        {
            Content = JsonContent.Create(new
            {
                includedTypes = new[] { "restaurant", "cafe", "fast_food_restaurant" },
                maxResultCount = 20,
                locationRestriction = new { circle = new { center = new { latitude = lat, longitude = lng }, radius = (double)radiusMeters } },
            }),
        };
        // Only ask for fields we use: the field mask decides the billing tier.
        req.Headers.Add("X-Goog-FieldMask", "places.id,places.displayName,places.primaryType,places.formattedAddress,places.location");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    internal static List<Place> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("places", out var places)) return [];
        var now = DateTimeOffset.UtcNow;
        return places.EnumerateArray().Select(p =>
        {
            var type = p.TryGetProperty("primaryType", out var t) ? t.GetString() ?? "restaurant" : "restaurant";
            var loc = p.GetProperty("location");
            return new Place
            {
                Id = "google:" + p.GetProperty("id").GetString(),
                Name = p.GetProperty("displayName").GetProperty("text").GetString() ?? "?",
                Cuisine = type.EndsWith("_restaurant") && type != "fast_food_restaurant"
                    ? type[..^"_restaurant".Length].Replace('_', ' ') : null,
                Category = type.Replace('_', ' '),
                Address = p.TryGetProperty("formattedAddress", out var a) ? a.GetString() : null,
                Lat = loc.GetProperty("latitude").GetDouble(), Lng = loc.GetProperty("longitude").GetDouble(), UpdatedAt = now,
            };
        }).ToList();
    }
}
