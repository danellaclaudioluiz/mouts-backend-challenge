using Ambev.DeveloperEvaluation.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Net;
using System.Text.Json;

namespace Ambev.DeveloperEvaluation.WebApi.Middleware
{
    /// <summary>
    /// Translates well-known exceptions into RFC 7807 problem responses
    /// (application/problem+json). Specific types are matched before
    /// generic ones; unhandled exceptions are returned as 500 without
    /// leaking stack traces — Serilog still logs the full detail.
    /// </summary>
    public class ValidationExceptionMiddleware
    {
        private const string ProblemContentType = "application/problem+json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly RequestDelegate _next;
        private readonly ILogger<ValidationExceptionMiddleware> _logger;

        public ValidationExceptionMiddleware(
            RequestDelegate next,
            ILogger<ValidationExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (ValidationException ex)
            {
                await WriteProblemAsync(context, BuildValidationProblem(context, ex));
            }
            catch (ResourceNotFoundException ex)
            {
                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Resource not found",
                    Type = "https://httpstatuses.io/404",
                    Detail = ex.Message,
                    Instance = context.Request.Path
                });
            }
            catch (ConflictException ex)
            {
                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflict",
                    Type = "https://httpstatuses.io/409",
                    Detail = ex.Message,
                    Instance = context.Request.Path
                });
            }
            catch (PreconditionFailedException ex)
            {
                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status412PreconditionFailed,
                    Title = "Precondition failed",
                    Type = "https://httpstatuses.io/412",
                    Detail = ex.Message,
                    Instance = context.Request.Path
                });
            }
            catch (DomainException ex)
            {
                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Domain rule violated",
                    Type = "https://httpstatuses.io/400",
                    Detail = ex.Message,
                    Instance = context.Request.Path
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Concurrent modification",
                    Type = "https://httpstatuses.io/409",
                    Detail = "The resource was modified by another request. Reload and retry.",
                    Instance = context.Request.Path
                });
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflict",
                    Type = "https://httpstatuses.io/409",
                    Detail = "The resource conflicts with an existing one (unique constraint violation).",
                    Instance = context.Request.Path
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Unauthorized",
                    Type = "https://httpstatuses.io/401",
                    Detail = ex.Message,
                    Instance = context.Request.Path
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception while processing {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                await WriteProblemAsync(context, new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal server error",
                    Type = "https://httpstatuses.io/500",
                    Detail = "An unexpected error occurred. Please contact support if it persists.",
                    Instance = context.Request.Path
                });
            }
        }

        private static ValidationProblemDetails BuildValidationProblem(HttpContext context, ValidationException ex)
        {
            var grouped = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => string.IsNullOrWhiteSpace(g.Key) ? "_" : g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());

            return new ValidationProblemDetails(grouped)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Type = "https://httpstatuses.io/400",
                Instance = context.Request.Path
            };
        }

        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            // Postgres SQLSTATE 23505 = unique_violation
            return ex.InnerException is PostgresException pg && pg.SqlState == "23505";
        }

        private static Task WriteProblemAsync(HttpContext context, ProblemDetails problem)
        {
            context.Response.ContentType = ProblemContentType;
            context.Response.StatusCode = problem.Status ?? (int)HttpStatusCode.InternalServerError;
            return context.Response.WriteAsync(JsonSerializer.Serialize(problem, problem.GetType(), JsonOptions));
        }
    }
}
