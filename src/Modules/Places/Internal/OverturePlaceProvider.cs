using System.Globalization;
using System.Net.Http.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace LunchSuggest.Modules.Places.Internal;

/// <summary>
/// Overture Maps Places: free, open POI data (Meta, Microsoft and others), much denser than OSM in many countries.
/// Read straight from Overture's public GeoParquet files on S3 with DuckDB; row-group bbox stats keep it to a small read.
/// </summary>
internal sealed class OverturePlaceProvider(HttpClient http, OvertureOptions options, ILogger<OverturePlaceProvider> log) : IPlaceProvider
{
    // Overture rates its own data; below this many entries are stale or junk.
    const double MinConfidence = 0.4;
    string? _release, _categoryExpr, _statusFilter;

    public string Source => "overture";

    public async Task<IReadOnlyList<Place>> FetchAsync(double lat, double lng, int radiusMeters, CancellationToken ct)
    {
        _release ??= options.Release ?? await LatestReleaseAsync(ct);
        var source = $"read_parquet('s3://overturemaps-us-west-2/release/{_release}/theme=places/type=place/*', hive_partitioning=1)";

        // DuckDB's .NET API is synchronous; keep it off the circuit's thread.
        return await Task.Run(() =>
        {
            using var con = new DuckDBConnection("DataSource=:memory:");
            con.Open();
            Exec(con, "INSTALL httpfs; LOAD httpfs; SET s3_region='us-west-2';");
            if (_categoryExpr is null) DetectSchema(con, source);

            var dLat = radiusMeters / 111_320d;
            var dLng = radiusMeters / (111_320d * Math.Cos(lat * Math.PI / 180));
            using var cmd = con.CreateCommand();
            cmd.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
                SELECT id, names.primary, {_categoryExpr} AS category, addresses[1].freeform,
                       (bbox.ymin + bbox.ymax) / 2, (bbox.xmin + bbox.xmax) / 2
                FROM {source}
                WHERE bbox.xmin BETWEEN {lng - dLng} AND {lng + dLng}
                  AND bbox.ymin BETWEEN {lat - dLat} AND {lat + dLat}
                  AND confidence >= {MinConfidence} {_statusFilter}
                  AND names.primary IS NOT NULL
                """);
            var now = DateTimeOffset.UtcNow;
            var list = new List<Place>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var category = r.IsDBNull(2) ? null : r.GetString(2);
                if (!IsFood(category)) continue;
                list.Add(new Place
                {
                    Id = "overture:" + r.GetString(0),
                    Name = r.GetString(1),
                    Category = Humanize(category!),
                    Cuisine = CuisineOf(category!),
                    Address = r.IsDBNull(3) ? null : r.GetString(3),
                    Lat = r.GetDouble(4), Lng = r.GetDouble(5), UpdatedAt = now,
                });
            }
            return list;
        }, ct);
    }

    async Task<string> LatestReleaseAsync(CancellationToken ct)
    {
        var catalog = await http.GetFromJsonAsync<StacCatalog>("https://stac.overturemaps.org/catalog.json", ct);
        var release = catalog?.Latest ?? throw new InvalidOperationException("Overture catalog has no 'latest' release.");
        log.LogInformation("Using Overture release {Release}", release);
        return release;
    }

    /// <summary>The places schema evolves (categories → basic_category, operating_status added); adapt instead of breaking.</summary>
    void DetectSchema(DuckDBConnection con, string source)
    {
        var columns = new Dictionary<string, string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"DESCRIBE SELECT * FROM {source}";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns[r.GetString(0)] = r.GetString(1);
        }
        // Most specific first: taxonomy.primary / categories.primary ("vietnamese_restaurant"), then coarse basic_category ("restaurant").
        var candidates = new[] { ("taxonomy", "taxonomy.primary"), ("categories", "categories.primary"), ("basic_category", "basic_category") }
            .Where(c => columns.ContainsKey(c.Item1)).Select(c => c.Item2).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("Overture places schema has no category column.");
        _categoryExpr = $"COALESCE({string.Join(", ", candidates)})";
        _statusFilter = columns.ContainsKey("operating_status") ? "AND coalesce(operating_status, 'open') = 'open'" : "";
        log.LogInformation("Overture category column: {Expr}", _categoryExpr);
    }

    static void Exec(DuckDBConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static bool IsFood(string? category) => FoodCategory.IsFood(category);

    internal static string Humanize(string category) => category.Replace('_', ' ');

    /// <summary>"vietnamese_restaurant" → "vietnamese"; generic categories carry no cuisine.</summary>
    internal static string? CuisineOf(string category) => category switch
    {
        "restaurant" or "cafe" or "coffee_shop" or "bakery" or "fast_food" or "fast_food_restaurant" or "family_style_restaurant" => null,
        _ when category.EndsWith("_restaurant") => Humanize(category[..^"_restaurant".Length]),
        _ => Humanize(category),
    };

    sealed record StacCatalog(string? Latest);
}

internal sealed record OvertureOptions(string? Release);
