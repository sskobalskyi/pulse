using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pulse.Simulator;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenProvider>();
builder.Services.AddHostedService<Worker>();

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("pulse-simulator"))
    .WithTracing(t => t.AddSource(Worker.TelemetryName).AddHttpClientInstrumentation())
    .WithMetrics(m => m.AddMeter(Worker.TelemetryName).AddHttpClientInstrumentation().AddRuntimeInstrumentation());

// Export over OTLP only when an endpoint is configured.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    otel.UseOtlpExporter();

builder.Build().Run();
