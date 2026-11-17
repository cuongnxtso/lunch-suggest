using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace LunchSuggest.Modules.Search.Internal;

internal sealed class SemanticIndex(
    IDbContextFactory<SearchDbContext> dbFactory,
    IEmbeddingGenerator<string, Embedding<float>> embedder) : ISemanticIndex
{
    // nomic-embed-text is trained with these task prefixes; other models just see a little extra text.
    const string DocPrefix = "search_document: ", QueryPrefix = "search_query: ";
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task IndexAsync(IReadOnlyCollection<SearchDocument> docs, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ids = docs.Select(d => d.Id).ToList();
            // No tracking: changed docs are deleted in SQL and re-added with the same Id below.
            var existing = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Text, ct);
            var todo = docs.Where(d => !existing.TryGetValue(d.Id, out var text) || text != d.Text).ToList();

            foreach (var batch in todo.Chunk(32))
            {
                var vectors = await embedder.GenerateAsync(batch.Select(d => DocPrefix + d.Text), cancellationToken: ct);
                var batchIds = batch.Select(d => d.Id).ToList();
                await db.Documents.Where(d => batchIds.Contains(d.Id)).ExecuteDeleteAsync(ct);
                db.Documents.AddRange(batch.Select((d, i) => new DocumentEmbedding
                {
                    Id = d.Id, Text = d.Text, Lat = d.Lat, Lng = d.Lng,
                    Embedding = new Vector(vectors[i].Vector),
                }));
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, double lat, double lng, int radiusMeters, int top = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = new Vector(await embedder.GenerateVectorAsync(QueryPrefix + query.Trim(), cancellationToken: ct));

        // Bounding box prefilter; good enough for lunch-walk radii.
        var dLat = radiusMeters / 111_320d;
        var dLng = radiusMeters / (111_320d * Math.Cos(lat * Math.PI / 180));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hits = await db.Documents
            .Where(d => d.Lat >= lat - dLat && d.Lat <= lat + dLat && d.Lng >= lng - dLng && d.Lng <= lng + dLng)
            .Select(d => new { d.Id, Distance = d.Embedding.CosineDistance(q) })
            .OrderBy(x => x.Distance)
            .Take(top)
            .ToListAsync(ct);
        return hits.Select(h => new SearchHit(h.Id, 1 - h.Distance)).ToList();
    }
}
