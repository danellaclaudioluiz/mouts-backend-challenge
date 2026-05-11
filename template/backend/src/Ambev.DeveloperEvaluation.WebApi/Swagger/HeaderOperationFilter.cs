using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Ambev.DeveloperEvaluation.WebApi.Swagger;

/// <summary>
/// Surfaces the cross-cutting headers (Authorization, Idempotency-Key,
/// If-Match) in Swagger UI so the generated docs / TypeScript clients see
/// the contract — they're handled by middleware / attributes and would
/// otherwise be invisible to Swashbuckle's reflection.
/// </summary>
public sealed class HeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.ApiDescription.HttpMethod?.ToUpperInvariant();
        var path = context.ApiDescription.RelativePath ?? string.Empty;
        var isSalesRoute = path.Contains("sales", StringComparison.OrdinalIgnoreCase);

        // Idempotency-Key is honoured by the IdempotencyMiddleware on every
        // POST. Document it as optional with a 256-char cap so client
        // generators include it in the typed SDK.
        if (method == "POST")
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Opaque client-generated key (≤ 256 chars). The middleware caches the first 2xx response for 24h and replays it byte-equal on retries with the same body. A different body under the same key returns 422.",
                Schema = new OpenApiSchema { Type = "string", MaxLength = 256 },
                Example = new OpenApiString("550e8400-e29b-41d4-a716-446655440000")
            });
        }

        // If-Match is honoured on PUT / DELETE / PATCH on Sales. The
        // controller's ParseIfMatch lifts it into the command's
        // ExpectedRowVersion and the handler returns 412 on mismatch.
        if (isSalesRoute && method is "PUT" or "DELETE" or "PATCH")
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "If-Match",
                In = ParameterLocation.Header,
                Required = false,
                Description = "ETag of the version the client expects to mutate (returned by the previous GET / POST / PATCH). A mismatch returns 412 Precondition Failed. Use \"*\" to bypass the check explicitly.",
                Schema = new OpenApiSchema { Type = "string" },
                Example = new OpenApiString("\"a3f\"")
            });
        }
    }
}
