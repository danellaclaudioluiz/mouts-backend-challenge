using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Enrichers.Span;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Exceptions.Core;
using Serilog.Exceptions.EntityFrameworkCore.Destructurers;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Templates;
using System.Diagnostics;

namespace Ambev.DeveloperEvaluation.Common.Logging;



/// <summary> Add default Logging configuration to project. This configuration supports Serilog logs with DataDog compatible output.</summary>
public static class LoggingExtension
{
    /// <summary>
    /// The destructuring options builder configured with default destructurers and a custom DbUpdateExceptionDestructurer.
    /// </summary>
    static readonly DestructuringOptionsBuilder _destructuringOptionsBuilder = new DestructuringOptionsBuilder()
        .WithDefaultDestructurers()
        .WithDestructurers([new DbUpdateExceptionDestructurer()]);

    /// <summary>
    /// Returns <c>true</c> when the event should be DROPPED. The intent is to
    /// silence the noisy 200-OK pings on /health while keeping every Warning,
    /// Error, Critical and Fatal event. The non-Information short-circuit
    /// MUST return <c>false</c> (= keep) — returning <c>true</c> here drops
    /// every error log in the application.
    /// </summary>
    static readonly Func<LogEvent, bool> _filterPredicate = logEvent =>
    {
        if (logEvent.Level != LogEventLevel.Information) return false;

        logEvent.Properties.TryGetValue("StatusCode", out var statusCode);
        logEvent.Properties.TryGetValue("Path", out var path);

        var isOk = statusCode == null || statusCode.ToString().Equals("200");
        var isHealthProbe = path?.ToString().Contains("/health") ?? false;

        return isOk && isHealthProbe;
    };

    /// <summary>
    /// This method configures the logging with commonly used features for DataDog integration.
    /// </summary>
    /// <param name="builder">The <see cref="WebApplicationBuilder" /> to add services to.</param>
    /// <returns>A <see cref="WebApplicationBuilder"/> that can be used to further configure the API services.</returns>
    /// <remarks>
    /// <para>Logging output are diferents on Debug and Release modes.</para>
    /// </remarks> 
    public static WebApplicationBuilder AddDefaultLogging(this WebApplicationBuilder builder)
    {
        Log.Logger = new LoggerConfiguration().CreateLogger();
        builder.Host.UseSerilog((hostingContext, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(hostingContext.Configuration)
                .Enrich.WithMachineName()
                .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
                .Enrich.WithProperty("Application", builder.Environment.ApplicationName)
                .Enrich.FromLogContext()
                // Pull TraceId and SpanId from the ambient OpenTelemetry Activity
                // so log lines join their traces in Tempo / Jaeger / Datadog
                // without manual correlation ids.
                .Enrich.WithSpan()
                .Enrich.WithExceptionDetails(_destructuringOptionsBuilder)
                .Filter.ByExcluding(_filterPredicate);

            const string OutputTemplate =
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] " +
                "trace={TraceId} span={SpanId} {SourceContext} {Message:lj}{NewLine}{Exception}";

            if (Debugger.IsAttached)
            {
                loggerConfiguration.Enrich.WithProperty("DebuggerAttached", Debugger.IsAttached);
                loggerConfiguration.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] trace={TraceId} [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    theme: SystemConsoleTheme.Colored);
            }
            else
            {
                loggerConfiguration
                    .WriteTo.Console(outputTemplate: OutputTemplate)
                    .WriteTo.File(
                        "logs/log-.txt",
                        rollingInterval: RollingInterval.Day,
                        // Cap rolling files at 14 days — without this Serilog
                        // keeps every daily log forever and the container's
                        // ephemeral FS fills up. 14d covers a typical
                        // incident-investigation window; ship to a real
                        // sink (Loki/Splunk) for longer retention.
                        retainedFileCountLimit: 14,
                        fileSizeLimitBytes: 50L * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        outputTemplate: OutputTemplate);
            }
        });

        builder.Services.AddLogging();

        return builder;
    }

    /// <summary>Adds middleware for Swagger documetation generation.</summary>
    /// <param name="app">The <see cref="WebApplication"/> instance this method extends.</param>
    /// <returns>The <see cref="WebApplication"/> for Swagger documentation.</returns>
    public static WebApplication UseDefaultLogging(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<Logger>>();

        var mode = Debugger.IsAttached ? "Debug" : "Release";
        logger.LogInformation("Logging enabled for '{Application}' on '{Environment}' - Mode: {Mode}", app.Environment.ApplicationName, app.Environment.EnvironmentName, mode);
        return app;

    }
}
