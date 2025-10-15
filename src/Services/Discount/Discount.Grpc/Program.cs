using Discount.Grpc.Data;
using Discount.Grpc.Services;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddDbContext<DiscountContext>(opts =>
        opts.UseSqlite(builder.Configuration.GetConnectionString("Database")));

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

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseMigration();
app.MapGrpcService<DiscountService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();
