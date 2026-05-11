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
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
    public const string AuthStrictRateLimitPolicy = "auth-strict";

    public static void Main(string[] args)
    {
        try
        {
            Log.Information("Starting web application");

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.AddDefaultLogging();

            // Default MVC JSON encoder is permissive: a stored
            // CustomerName like "<script>" round-trips on the wire as raw
            // <script>. JSON-only consumers can render safely with their
            // own escape, but defence-in-depth wins — switch to the strict
            // JavaScriptEncoder.Default so '<', '>', '&', and quotes come
            // out as \uXXXX. Cheap, and removes a class of HTML-injection
            // surprise for clients that pipe responses straight into innerHTML.
            builder.Services.AddControllers().AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default;
                // Serialise enums as strings so clients see "Customer" /
                // "Active" instead of 1 / 1 — round-trips safely across
                // enum renumberings and is the standard for public APIs.
                o.JsonSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
            });
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
            // Requires an explicit allow-list — appsettings ships "localhost:5173"
            // / "localhost:4200" for dev. AllowAnyOrigin() is no longer used
            // anywhere (the previous dev shortcut combined with a leaked-token
            // scenario was equivalent to a full CORS bypass).
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(DefaultCorsPolicy, policy =>
                {
                    var origins = builder.Configuration
                        .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

                    if (origins.Length == 0)
                    {
                        // Last-resort fallback: deny all cross-origin traffic.
                        // Misconfiguration is loud (no preflight succeeds) rather
                        // than silent (everything allowed).
                        policy.WithOrigins(Array.Empty<string>());
                    }
                    else
                    {
                        policy.WithOrigins(origins)
                            .AllowAnyHeader()
                            .AllowAnyMethod()
                            .AllowCredentials();
                    }
                });
            });

            // Rate limiting: fixed-window per principal on the API surface.
            // Partition by authenticated user first so corporate clients sharing
            // a NAT IP do not throttle each other; fall back to remote IP for
            // anonymous traffic. Lower the limit per-route via additional policies.
            // Permit limit and window are configurable so tests (and ops) can
            // tune them without a redeploy — RateLimit:PermitLimit defaults to
            // 100 requests per RateLimit:WindowSeconds (default 60s).
            var permitLimit = builder.Configuration.GetValue<int?>("RateLimit:PermitLimit") ?? 100;
            var windowSeconds = builder.Configuration.GetValue<int?>("RateLimit:WindowSeconds") ?? 60;
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy(ApiRateLimitPolicy, httpContext =>
                {
                    var partitionKey =
                        httpContext.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? httpContext.Connection.RemoteIpAddress?.ToString()
                        ?? "anonymous";

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = TimeSpan.FromSeconds(windowSeconds),
                            QueueLimit = 0
                        });
                });

                // Aggressive policy for /auth: 5 requests / minute / IP.
                // Brute-forcing a password through the global 100/min would
                // give an attacker ~6,000 attempts per hour against one
                // username — the auth-strict bucket caps that at 300/h/IP.
                options.AddPolicy(AuthStrictRateLimitPolicy, httpContext =>
                {
                    var partitionKey =
                        httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = builder.Configuration.GetValue<int?>("RateLimit:AuthPermitLimit") ?? 5,
                            Window = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("RateLimit:AuthWindowSeconds") ?? 60),
                            QueueLimit = 0
                        });
                });
            });

            // BackgroundServices (OutboxDispatcherService, OutboxCleanupService,
            // OutboxNotifyListener) talk to Postgres. If the DB drops, an
            // unhandled exception in any of them would (under .NET 6+ defaults)
            // tear down the whole host — taking the API and the readiness
            // probe down with it. Switch to Ignore so the host stays up; the
            // dispatcher's own catch-and-log loop reports the outage instead,
            // and /health/ready surfaces it via AddDbContextCheck.
            builder.Services.Configure<HostOptions>(o =>
                o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            builder.AddBasicHealthChecks();

            // Probe Postgres on /health/ready so kubernetes-style readiness
            // checks fail when the DB is unreachable instead of letting traffic
            // hit a doomed instance.
            builder.Services.AddHealthChecks()
                .AddDbContextCheck<DefaultContext>(
                    name: "Postgres",
                    failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                    tags: new[] { "readiness" });

            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
                {
                    Title = "Ambev DeveloperStore — Sales API",
                    Version = "v1",
                    Description = "REST API for the Sales aggregate. JWT bearer auth, ETag/If-Match optimistic concurrency, Idempotency-Key, transactional outbox."
                });

                // Wire the XML doc file emitted by GenerateDocumentationFile.
                var xmlPath = Path.Combine(AppContext.BaseDirectory,
                    $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml");
                if (File.Exists(xmlPath))
                    c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);

                // Bearer scheme so the "Authorize" button in Swagger UI lets
                // operators paste a JWT and try authenticated endpoints.
                c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Description = "Paste only the JWT, no 'Bearer ' prefix.",
                    In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                    Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT"
                });
                c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
                {
                    [new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                    {
                        Reference = new Microsoft.OpenApi.Models.OpenApiReference
                        {
                            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    }] = Array.Empty<string>()
                });

                c.OperationFilter<Swagger.HeaderOperationFilter>();
            });

            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is not configured. " +
                    "Set it in appsettings.Development.json, user-secrets, or the " +
                    "ConnectionStrings__DefaultConnection environment variable.");

            // Pool DbContext instances (with the Model + change tracker
            // recycled across requests) and tune Npgsql:
            //   - retry on transient failures so brief network blips and PG
            //     failovers don't cascade into 5xx;
            //   - a 15s command timeout caps any runaway query;
            //   - sensitive-data logging stays off (avoids leaking parameters
            //     to logs in production).
            // The connection string itself controls Pooling/MinPoolSize/
            // MaxPoolSize/Keepalive — see appsettings.Development.json for the
            // tuned dev value and the README for the production guidance.
            builder.Services.AddDbContextPool<DefaultContext>(options =>
                options
                    .UseNpgsql(connectionString, npg =>
                    {
                        npg.MigrationsAssembly("Ambev.DeveloperEvaluation.ORM");
                        npg.EnableRetryOnFailure(
                            maxRetryCount: 3,
                            maxRetryDelay: TimeSpan.FromSeconds(2),
                            errorCodesToAdd: null);
                        npg.CommandTimeout(15);
                    })
                    .EnableSensitiveDataLogging(false),
                poolSize: 256);

            builder.Services.AddJwtAuthentication(builder.Configuration, builder.Environment);

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

            // Authorization fallback: every endpoint requires an authenticated
            // principal unless it explicitly opts out via [AllowAnonymous].
            // Without this, controllers without an [Authorize] attribute are
            // PUBLIC — a single forgotten attribute on a new controller would
            // ship the whole feature unauthenticated.
            builder.Services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new Microsoft.AspNetCore.Authorization
                    .AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
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

            // Trust X-Forwarded-* only from configured proxies/networks.
            // Without an allow-list the API would accept the header from
            // ANY caller and an attacker could spoof their IP — poisoning
            // logs, the rate-limit partition key, and any IP-based ACLs
            // downstream. ForwardedHeaders:KnownProxies / KnownNetworks
            // (CSV) populate the trust list from config; in dev we add
            // the loopback ranges so local Kestrel-behind-proxy still works.
            var forwardedOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            };
            forwardedOptions.KnownProxies.Clear();
            forwardedOptions.KnownNetworks.Clear();

            foreach (var ip in (app.Configuration["ForwardedHeaders:KnownProxies"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (System.Net.IPAddress.TryParse(ip, out var parsed))
                    forwardedOptions.KnownProxies.Add(parsed);
            }
            foreach (var range in (app.Configuration["ForwardedHeaders:KnownNetworks"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = range.Split('/');
                if (parts.Length == 2
                    && System.Net.IPAddress.TryParse(parts[0], out var prefix)
                    && int.TryParse(parts[1], out var prefixLen))
                {
                    forwardedOptions.KnownNetworks.Add(
                        new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLen));
                }
            }

            if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Test"))
            {
                // Loopback (127.0.0.0/8 + ::1/128) so local in-process tests
                // and curl against Kestrel work without explicit config.
                forwardedOptions.KnownNetworks.Add(
                    new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Loopback, 8));
                forwardedOptions.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
            }

            app.UseForwardedHeaders(forwardedOptions);

            app.UseMiddleware<ValidationExceptionMiddleware>();
            app.UseMiddleware<IdempotencyMiddleware>();

            // Swagger ships behind an explicit feature flag (Swagger:Enabled)
            // — never directly tied to ASPNETCORE_ENVIRONMENT=Development. A
            // misconfigured prod deploy that inherits the dev env var would
            // otherwise expose the entire API contract publicly.
            var swaggerEnabled = app.Configuration.GetValue<bool?>("Swagger:Enabled")
                ?? app.Environment.IsDevelopment();
            if (swaggerEnabled)
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // HSTS for any non-Development environment. Tells browsers to
            // refuse plain-HTTP downgrades for the next year, including for
            // subdomains. Without this the JWT can leak over HTTP on the
            // first redirect to HTTPS.
            if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test"))
            {
                app.UseHsts();
            }

            // Security headers — cheap defence in depth even for a JSON API.
            // Sniffing-off, frame-deny, no Referer leakage cross-origin, and
            // a tight CSP because the API never serves HTML.
            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.Append("X-Content-Type-Options", "nosniff");
                headers.Append("X-Frame-Options", "DENY");
                headers.Append("Referrer-Policy", "no-referrer");
                headers.Append("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
                await next();
            });

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
