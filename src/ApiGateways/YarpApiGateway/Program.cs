using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    rateLimiterOptions.AddFixedWindowLimiter("fixed", options =>
    {
        options.Window = TimeSpan.FromSeconds(10);
        options.PermitLimit = 5;
    });
});

var app = builder.Build();

// OpenTelemetry logging
var serviceName = builder.Environment.ApplicationName;
var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
var environment = builder.Environment.EnvironmentName;

builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    options.SetResourceBuilder(
        ResourceBuilder.CreateDefault()
            .AddService(serviceName: serviceName, serviceVersion: serviceVersion, serviceInstanceId: Environment.MachineName)
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object?>("deployment.environment", environment)
            }));

    options.AddOtlpExporter(otlp =>
    {
        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";
        otlp.Endpoint = new Uri(endpoint);
        otlp.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
    });
});

// Configure the HTTP request pipeline.
app.UseRateLimiter();

app.MapReverseProxy();

app.Run();
