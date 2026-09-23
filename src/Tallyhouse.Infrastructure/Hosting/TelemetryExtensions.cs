using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tallyhouse.Application;

namespace Tallyhouse.Infrastructure.Hosting;

public static class TelemetryExtensions
{
    /// <summary>
    /// Traces and metrics for one host. Exported over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set,
    /// which is the standard variable, so pointing the stack at a collector is configuration and not code.
    /// </summary>
    public static IHostApplicationBuilder AddTallyhouseTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OpenTelemetryBuilder telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(Telemetry.Name)
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(Telemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
