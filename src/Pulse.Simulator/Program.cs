using Pulse.Simulator;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
