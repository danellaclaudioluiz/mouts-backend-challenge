using Ambev.DeveloperEvaluation.Application;
using Ambev.DeveloperEvaluation.Common.HealthChecks;
using Ambev.DeveloperEvaluation.Common.Logging;
using Ambev.DeveloperEvaluation.Common.Security;
using Ambev.DeveloperEvaluation.Common.Validation;
using Ambev.DeveloperEvaluation.IoC;
using Ambev.DeveloperEvaluation.ORM;
using Ambev.DeveloperEvaluation.WebApi.Middleware;
using Asp.Versioning;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using System.Threading.RateLimiting;

namespace Ambev.DeveloperEvaluation.WebApi;

public class Program
{
    public const string DefaultCorsPolicy = "default";
    public const string ApiRateLimitPolicy = "api";

    public static void Main(string[] args)
    {
        try
        {
            Log.Information("Starting web application");

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.AddDefaultLogging();

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();

            // Idempotency cache: Redis when configured (multi-pod safe), in-memory
            // fallback otherwise. The 'Redis' connection string keeps the
            // multi-instance Idempotency-Key story working under a load balancer.
            var redisConnection = builder.Configuration.GetConnectionString("Redis");
            if (!string.IsNullOrWhiteSpace(redisConnection))
            {
                builder.Services.AddStackExchangeRedisCache(opts =>
                {
                    opts.Configuration = redisConnection;
                    opts.InstanceName = "ambev-sales:";
                });
            }
            else
            {
                builder.Services.AddDistributedMemoryCache();
            }

            builder.Services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
            }).AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            });

            // CORS — restrictive by default (ConfiguredOrigins config key);
            // wide-open only in Development for local Swagger UI / dev front-ends.
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(DefaultCorsPolicy, policy =>
                {
                    var origins = builder.Configuration
                        .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

                    if (builder.Environment.IsDevelopment() && origins.Length == 0)
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    else
                        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()
                            .AllowCredentials();
                });
            });

            // Rate limiting: a fixed-window per-IP bucket on the API surface.
            // Lower the limit per-route or by user via additional policies.
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy(ApiRateLimitPolicy, httpContext =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 100,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        }));
            });

            builder.AddBasicHealthChecks();

            // Probe Postgres on /health/ready so kubernetes-style readiness
            // checks fail when the DB is unreachable instead of letting traffic
            // hit a doomed instance.
            builder.Services.AddHealthChecks()
                .AddDbContextCheck<DefaultContext>(
                    name: "Postgres",
                    failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                    tags: new[] { "readiness" });

            builder.Services.AddSwaggerGen();

            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is not configured. " +
                    "Set it in appsettings.Development.json, user-secrets, or the " +
                    "ConnectionStrings__DefaultConnection environment variable.");

            builder.Services.AddDbContext<DefaultContext>(options =>
                options.UseNpgsql(
                    connectionString,
                    b => b.MigrationsAssembly("Ambev.DeveloperEvaluation.ORM")
                )
            );

            builder.Services.AddJwtAuthentication(builder.Configuration);

            // OpenTelemetry — traces + metrics for the request pipeline, EF
            // Core SQL, and outbound HTTP. Exports via OTLP when an endpoint is
            // configured (OTEL_EXPORTER_OTLP_ENDPOINT env var or
            // OpenTelemetry:OtlpEndpoint config); otherwise the providers are
            // registered without an exporter so application code can still
            // emit spans/metrics for in-process listeners.
            var otlpEndpoint =
                builder.Configuration["OpenTelemetry:OtlpEndpoint"] ??
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(
                    serviceName: "ambev.developerevaluation.webapi",
                    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
                .WithTracing(tracing =>
                {
                    tracing
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddEntityFrameworkCoreInstrumentation();
                    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                        tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                })
                .WithMetrics(metrics =>
                {
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();
                    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                        metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                });

            builder.RegisterDependencies();

            builder.Services.AddAutoMapper(typeof(Program).Assembly, typeof(ApplicationLayer).Assembly);

            builder.Services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblies(
                    typeof(ApplicationLayer).Assembly,
                    typeof(Program).Assembly
                );
            });

            builder.Services.AddValidatorsFromAssemblies(new[]
            {
                typeof(ApplicationLayer).Assembly,
                typeof(Program).Assembly
            });
            builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

            var app = builder.Build();
            app.UseMiddleware<ValidationExceptionMiddleware>();
            app.UseMiddleware<IdempotencyMiddleware>();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseCors(DefaultCorsPolicy);
            app.UseRateLimiter();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseBasicHealthChecks();

            app.MapControllers().RequireRateLimiting(ApiRateLimitPolicy);

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
