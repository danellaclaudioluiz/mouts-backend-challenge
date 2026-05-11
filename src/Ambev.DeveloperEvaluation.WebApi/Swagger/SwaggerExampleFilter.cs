using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Ambev.DeveloperEvaluation.WebApi.Swagger;

/// <summary>
/// Replaces the placeholder <c>"string"</c> example values Swashbuckle
/// emits by default with realistic, validator-compliant samples for the
/// request DTOs reviewers actually exercise. Without this the Swagger
/// "Example Value" panel shows
/// <c>{ "username": "string", "email": "string" }</c> — useless against
/// a real validator that wants
/// <c>Test@123</c> for the password and <c>+5511...</c> for the phone.
/// </summary>
/// <remarks>
/// Per-DTO examples live in this single filter on purpose: keeps the
/// DTO classes free of Swagger attributes (Domain/Application layers
/// stay agnostic of the docs framework) and means every example is
/// reviewed in one place.
/// </remarks>
public sealed class SwaggerExampleFilter : ISchemaFilter
{
    private static readonly Dictionary<string, Action<OpenApiSchema>> Examples = new()
    {
        ["CreateUserRequest"] = s => s.Example = new OpenApiObject
        {
            ["username"] = new OpenApiString("alice"),
            ["password"] = new OpenApiString("Str0ng!Passw0rd"),
            ["email"] = new OpenApiString("alice@example.com"),
            ["phone"] = new OpenApiString("+5511999998888")
        },
        ["AuthenticateUserRequest"] = s => s.Example = new OpenApiObject
        {
            ["email"] = new OpenApiString("alice@example.com"),
            ["password"] = new OpenApiString("Str0ng!Passw0rd")
        },
        ["RefreshTokenRequest"] = s => s.Example = new OpenApiObject
        {
            ["refreshToken"] = new OpenApiString("aT3-q5K9PvP-aTxLpFcMOyL_KsP5w6h2nUcVm9Dr5oA")
        },
        ["CreateSaleRequest"] = s => s.Example = new OpenApiObject
        {
            ["saleNumber"] = new OpenApiString("S-2026-000123"),
            ["saleDate"] = new OpenApiString("2026-05-11T12:00:00Z"),
            ["customerId"] = new OpenApiString("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            ["customerName"] = new OpenApiString("Acme Distribuidora Ltda"),
            ["branchId"] = new OpenApiString("9e8b6c4d-2a1f-4b3e-9c4d-7e6a8b9c0d1e"),
            ["branchName"] = new OpenApiString("Filial São Paulo Centro"),
            ["items"] = new OpenApiArray
            {
                new OpenApiObject
                {
                    ["productId"] = new OpenApiString("11111111-2222-3333-4444-555555555555"),
                    ["productName"] = new OpenApiString("Cerveja Pilsen 350ml"),
                    ["quantity"] = new OpenApiInteger(12),
                    ["unitPrice"] = new OpenApiDouble(4.50)
                },
                new OpenApiObject
                {
                    ["productId"] = new OpenApiString("66666666-7777-8888-9999-aaaaaaaaaaaa"),
                    ["productName"] = new OpenApiString("Refrigerante Cola 2L"),
                    ["quantity"] = new OpenApiInteger(6),
                    ["unitPrice"] = new OpenApiDouble(9.90)
                }
            }
        },
        ["UpdateSaleRequest"] = s => s.Example = new OpenApiObject
        {
            ["saleDate"] = new OpenApiString("2026-05-11T12:00:00Z"),
            ["customerId"] = new OpenApiString("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            ["customerName"] = new OpenApiString("Acme Distribuidora Ltda"),
            ["branchId"] = new OpenApiString("9e8b6c4d-2a1f-4b3e-9c4d-7e6a8b9c0d1e"),
            ["branchName"] = new OpenApiString("Filial São Paulo Centro"),
            ["items"] = new OpenApiArray
            {
                new OpenApiObject
                {
                    ["productId"] = new OpenApiString("11111111-2222-3333-4444-555555555555"),
                    ["productName"] = new OpenApiString("Cerveja Pilsen 350ml"),
                    ["quantity"] = new OpenApiInteger(20),
                    ["unitPrice"] = new OpenApiDouble(4.50)
                }
            }
        },
        ["CreateSaleItemDto"] = s => s.Example = new OpenApiObject
        {
            ["productId"] = new OpenApiString("11111111-2222-3333-4444-555555555555"),
            ["productName"] = new OpenApiString("Cerveja Pilsen 350ml"),
            ["quantity"] = new OpenApiInteger(12),
            ["unitPrice"] = new OpenApiDouble(4.50)
        }
    };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var typeName = context.Type.Name;
        if (Examples.TryGetValue(typeName, out var apply))
            apply(schema);
    }
}
