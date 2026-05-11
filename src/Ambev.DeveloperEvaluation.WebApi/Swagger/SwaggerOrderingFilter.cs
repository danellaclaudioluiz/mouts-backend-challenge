using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Ambev.DeveloperEvaluation.WebApi.Swagger;

/// <summary>
/// Forces a deterministic, reading-friendly order in the Swagger UI:
///
/// <list type="bullet">
///   <item><b>Tag groups</b>: Auth → Users → Sales (natural onboarding
///         flow). Without this Swashbuckle falls back to insertion or
///         alphabetical order and Auth ends up sandwiched between
///         Sales and Users.</item>
///   <item><b>HTTP verbs within each tag</b>: POST → GET → PUT → PATCH
///         → DELETE (write-then-read-then-mutate-then-delete). Swagger
///         UI's default is the dictionary key order of
///         <c>OpenApiPathItem.Operations</c>, which Swashbuckle fills
///         in the order ASP.NET discovered the actions —
///         alphabetical-by-method-name for the most part, so DELETE
///         appears above GET above POST. Confusing for a reader who
///         expects "first create, then read, then change".</item>
/// </list>
/// </summary>
/// <remarks>
/// <see cref="SwaggerGenOptions.OrderActionsBy"/> only affects the order
/// inside <c>ApiDescriptions</c>; the Swagger UI then groups by tag and
/// reads <see cref="OpenApiPaths"/> + <see cref="OpenApiPathItem.Operations"/>
/// dictionary iteration order. The only reliable knob is a
/// <see cref="IDocumentFilter"/> that rewrites both after Swashbuckle
/// has materialised the doc.
/// </remarks>
public sealed class SwaggerOrderingFilter : IDocumentFilter
{
    private static readonly string[] TagOrder = { "Auth", "Users", "Sales" };

    /// <summary>
    /// Canonical tag metadata. Swashbuckle only auto-promotes a
    /// controller's XML <c>&lt;summary&gt;</c> into a tag entry when
    /// it sees one; SalesController lacks a class-level summary, so
    /// without this the Swagger UI shows "Sales" with no description.
    /// Declaring all three here also guarantees the group order is
    /// stable even when Swashbuckle finds no XML at all.
    /// </summary>
    private static readonly OpenApiTag[] CanonicalTags =
    {
        new() { Name = "Auth",  Description = "Authentication & refresh-token rotation." },
        new() { Name = "Users", Description = "Self-service signup + user lookup." },
        new() { Name = "Sales", Description = "Sales aggregate CRUD with ETag/If-Match concurrency, Idempotency-Key, and transactional outbox." }
    };

    private static readonly OperationType[] MethodOrder =
    {
        OperationType.Post,
        OperationType.Get,
        OperationType.Put,
        OperationType.Patch,
        OperationType.Delete
    };

    public void Apply(OpenApiDocument doc, DocumentFilterContext context)
    {
        ReorderTags(doc);
        ReorderPathsAndOperations(doc);
    }

    private static int TagIndex(string? name)
    {
        if (string.IsNullOrEmpty(name)) return int.MaxValue;
        var idx = Array.IndexOf(TagOrder, name);
        return idx < 0 ? int.MaxValue : idx;
    }

    private static int MethodIndex(OperationType method)
    {
        var idx = Array.IndexOf(MethodOrder, method);
        return idx < 0 ? int.MaxValue : idx;
    }

    private static void ReorderTags(OpenApiDocument doc)
    {
        // Build a fresh tag list from the canonical metadata so every
        // group has a description, then append any other tag Swashbuckle
        // discovered (defensive — there shouldn't be any).
        doc.Tags ??= new List<OpenApiTag>();
        var byName = doc.Tags.ToDictionary(
            t => t.Name ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        var rebuilt = new List<OpenApiTag>();
        foreach (var canonical in CanonicalTags)
        {
            // Prefer the canonical description even if Swashbuckle
            // promoted a less-rich one from XML comments.
            rebuilt.Add(new OpenApiTag
            {
                Name = canonical.Name,
                Description = canonical.Description
            });
            byName.Remove(canonical.Name);
        }
        foreach (var stragglers in byName.Values
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            rebuilt.Add(stragglers);

        doc.Tags = rebuilt;
    }

    private static void ReorderPathsAndOperations(OpenApiDocument doc)
    {
        if (doc.Paths is null || doc.Paths.Count == 0) return;

        // Sort paths by the tag of their first operation, then alphabetically.
        var orderedKeys = doc.Paths
            .OrderBy(p => TagIndex(PrimaryTag(p.Value)))
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key)
            .ToList();

        var rebuilt = new OpenApiPaths();
        foreach (var key in orderedKeys)
        {
            var pathItem = doc.Paths[key];
            pathItem.Operations = ReorderOperations(pathItem.Operations);
            rebuilt[key] = pathItem;
        }
        doc.Paths = rebuilt;
    }

    private static string? PrimaryTag(OpenApiPathItem pathItem)
    {
        return pathItem.Operations.Values
            .SelectMany(op => op.Tags ?? Enumerable.Empty<OpenApiTag>())
            .Select(t => t.Name)
            .FirstOrDefault();
    }

    private static IDictionary<OperationType, OpenApiOperation> ReorderOperations(
        IDictionary<OperationType, OpenApiOperation> ops)
    {
        if (ops.Count <= 1) return ops;
        var rebuilt = new Dictionary<OperationType, OpenApiOperation>();
        foreach (var pair in ops.OrderBy(o => MethodIndex(o.Key)))
            rebuilt[pair.Key] = pair.Value;
        return rebuilt;
    }
}
