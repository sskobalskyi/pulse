using Confluent.Kafka;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pulse.Api.Auth;
using Pulse.Api.Endpoints;
using Pulse.Api.Hubs;
using Pulse.Api.Messaging;
using Pulse.Api.Observability;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<AlertRulesOptions>(builder.Configuration.GetSection("Alerts"));
builder.Services.Configure<SilenceOptions>(builder.Configuration.GetSection("Alerts:NoData"));

builder.Services.AddSingleton(NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Timescale") ?? throw new InvalidOperationException("Missing ConnectionStrings:Timescale")));
builder.Services.AddSingleton<PulseDb>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<DeviceRuleStore>();
builder.Services.AddSingleton<LiveFanout>();

builder.Services.AddPulseAuth();
builder.Services.AddClaimRateLimiting();

builder.Services.AddSingleton<IProducer<string, string>>(_ =>
{
    var kafka = builder.Configuration.GetSection("Kafka").Get<KafkaOptions>() ?? new KafkaOptions();
    return new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = kafka.BootstrapServers,
        Acks = Acks.All,
        EnableIdempotence = true,
        LingerMs = 5,
    }).Build();
});

// Registered first: hosted services start in order, so the schema is current before any consumer runs.
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddHostedService<MigrationService>();
builder.Services.AddHostedService<KafkaTopicInitializer>();
builder.Services.AddHostedService<TimescaleWriter>();
builder.Services.AddHostedService<LivePublisher>();
builder.Services.AddHostedService<AlertEvaluator>();
builder.Services.AddHostedService<SilenceMonitor>();

builder.Services.AddSignalR();
// Every problem response carries a stable `code`, as the v2 contract requires, including framework-generated ones.
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions.TryAdd("code", ApiProblems.CodeFor(ctx.ProblemDetails.Status)));
builder.Services.AddHealthChecks();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("pulse-api"))
    .WithTracing(t => t
        .AddSource(PulseTelemetry.Name)
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => ctx.Request.Path != "/health")
        .AddNpgsql())
    .WithMetrics(m => m
        .AddMeter(PulseTelemetry.Name, "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddNpgsqlInstrumentation());

// Export over OTLP only when an endpoint is configured, so a bare `dotnet run` stays quiet.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    otel.UseOtlpExporter();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapTelemetryEndpoints();
app.MapDeviceRegistry();
app.MapHub<LiveHub>("/hubs/live").RequireAuthorization(Policies.Caller);

app.Run();

public partial class Program;
