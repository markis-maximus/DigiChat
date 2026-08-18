using System.Text.Json;
using System.Text.Json.Serialization;
using DigiChat.Domain;
using DigiChat.Domain.Entities;
using DigiChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DigiChat.Infrastructure.Seeding;

/// <summary>
/// Loads the editable lineage roster (data/lineages.json) and upserts it into
/// the database at startup. Matching is by lineage slug and form stage, so the
/// file can be edited or wholly replaced without code changes. Lineages removed
/// from the file are disabled, never deleted (history must survive).
/// </summary>
public class LineageSeeder(ILogger<LineageSeeder> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task SeedAsync(DigiChatDbContext db, string lineageFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(lineageFilePath))
            throw new FileNotFoundException(
                $"Lineage roster not found at '{lineageFilePath}'. Set Data:LineageFile in configuration.", lineageFilePath);
        if (new FileInfo(lineageFilePath).Length > 4 * 1024 * 1024)
            throw new InvalidOperationException("Lineage roster is unexpectedly larger than 4 MiB.");

        await using var stream = File.OpenRead(lineageFilePath);
        var doc = await JsonSerializer.DeserializeAsync<LineageFile>(stream, JsonOptions, ct)
                  ?? throw new InvalidOperationException("Lineage roster file deserialized to null.");
        var lineages = doc.Lineages
                       ?? throw new InvalidOperationException("Lineage roster has no 'lineages' array.");

        if (lineages.Count is < 1 or > 500)
            throw new InvalidOperationException("Lineage roster must contain between 1 and 500 entries.");
        var duplicateSlug = lineages
            .GroupBy(l => l.Slug, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicateSlug is not null)
            throw new InvalidOperationException($"Lineage roster contains duplicate slug '{duplicateSlug}'.");
        var duplicateOrder = lineages.GroupBy(l => l.OrderIndex)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicateOrder is not null)
            throw new InvalidOperationException($"Lineage roster contains duplicate orderIndex {duplicateOrder}.");

        var existingRows = await db.Lineages.Include(l => l.Forms).ToListAsync(ct);
        var existing = existingRows.ToDictionary(l => l.Slug, StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0;

        foreach (var seed in lineages)
        {
            if (string.IsNullOrWhiteSpace(seed.Slug))
                throw new InvalidOperationException("Lineage roster contains an entry without a slug.");
            if (seed.OrderIndex is < 1 or > 500)
                throw new InvalidOperationException(
                    $"Lineage '{seed.Slug}' has orderIndex {seed.OrderIndex}; expected 1-500.");
            if (seed.Slug.Length > 64 || (seed.Name?.Length ?? 0) > 128
                || (seed.SourceMedia?.Length ?? 0) > 128 || (seed.Canonicality?.Length ?? 0) > 64)
                throw new InvalidOperationException(
                    $"Lineage '{seed.Slug}' exceeds a database text-length limit.");

            if (!existing.TryGetValue(seed.Slug, out var lineage))
            {
                lineage = new Lineage { Slug = seed.Slug };
                db.Lineages.Add(lineage);
                existing[seed.Slug] = lineage;
                added++;
            }
            else
            {
                updated++;
            }

            lineage.Slug = seed.Slug;
            lineage.Name = seed.Name ?? seed.Slug;
            lineage.OrderIndex = seed.OrderIndex;
            lineage.Enabled = seed.Enabled;
            lineage.SourceMedia = seed.SourceMedia;
            lineage.Canonicality = seed.Canonicality;
            lineage.Notes = seed.Notes;

            if (seed.Forms is null)
                throw new InvalidOperationException($"Lineage '{seed.Slug}' has no forms object.");
            foreach (var (stage, formName) in seed.StageForms())
            {
                if (formName.Length > 128)
                    throw new InvalidOperationException(
                        $"Lineage '{seed.Slug}' has a {stage} form name longer than 128 characters.");
                var form = lineage.Forms.FirstOrDefault(f => f.Stage == stage);
                if (form is null)
                {
                    form = new DigimonForm { Stage = stage, Lineage = lineage };
                    lineage.Forms.Add(form);
                }
                form.Name = formName;
                form.AssetKey = Slugify(formName);
            }
        }

        // Lineages present in the DB but missing from the file: disable, keep history.
        var fileSlugs = lineages.Select(l => l.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var orphan in existing.Values.Where(l => !fileSlugs.Contains(l.Slug) && l.Enabled))
        {
            orphan.Enabled = false;
            logger.LogWarning("Lineage {Slug} is missing from the roster file and has been disabled", orphan.Slug);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Lineage roster seeded from {File}: {Added} added, {Updated} updated, {Total} total",
            lineageFilePath, added, updated, existing.Count);
    }

    /// <summary>"Coredramon (Blue)" → "coredramon-blue"; used as the asset manifest key.</summary>
    public static string Slugify(string name)
    {
        var chars = name.ToLowerInvariant()
            // Asset keys are deliberately portable ASCII paths. Keep this in
            // lockstep with the three Node asset tools.
            .Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private sealed record LineageFile([property: JsonPropertyName("lineages")] List<LineageSeed>? Lineages);

    private sealed record LineageSeed(
        string Slug,
        string? Name,
        int OrderIndex,
        bool Enabled,
        string? SourceMedia,
        string? Canonicality,
        string? Notes,
        FormsSeed? Forms)
    {
        public IEnumerable<(DigivolutionStage Stage, string Name)> StageForms()
        {
            var forms = Forms
                        ?? throw new InvalidOperationException($"Lineage '{Slug}' has no forms object.");
            yield return (DigivolutionStage.Fresh, Required(forms.Fresh, "fresh"));
            yield return (DigivolutionStage.InTraining, Required(forms.InTraining, "inTraining"));
            yield return (DigivolutionStage.Rookie, Required(forms.Rookie, "rookie"));
            yield return (DigivolutionStage.Champion, Required(forms.Champion, "champion"));
            yield return (DigivolutionStage.Ultimate, Required(forms.Ultimate, "ultimate"));
        }

        private string Required(string? value, string stage) =>
            !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"Lineage '{Slug}' is missing its '{stage}' form.");
    }

    private sealed record FormsSeed(string? Fresh, string? InTraining, string? Rookie, string? Champion, string? Ultimate);
}
