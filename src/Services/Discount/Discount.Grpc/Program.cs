using Discount.Grpc.Data;
using Discount.Grpc.Services;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using System.Reflection;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddDbContext<DiscountContext>(opts =>
        opts.UseSqlite(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddHealthChecks();

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
                new KeyValuePair<string, object>("deployment.environment", environment)
            }));

    options.AddOtlpExporter(otlp =>
    {
        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";
        otlp.Endpoint = new Uri(endpoint);
        otlp.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
    });
});

builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);


// OpenTelemetry tracing (captures gRPC server via AspNetCore)
builder.Services.AddOpenTelemetry()
    .WithTracing(tracer =>
    {
        tracer
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;
                options.EnrichWithHttpRequest = (activity, req) =>
                {
                    activity.SetTag("enduser.id", req.HttpContext.User?.Identity?.Name ?? "anonymous");
                    activity.SetTag("http.client_ip", req.HttpContext.Connection.RemoteIpAddress?.ToString());
                    activity.SetTag("http.user_agent", req.Headers.UserAgent.ToString());
                };
                options.EnrichWithHttpResponse = (activity, res) =>
                {
                    if (res.ContentLength.HasValue)
                        activity.SetTag("http.response_content_length", res.ContentLength.Value);
                };
                options.EnrichWithException = (activity, ex) => { };
            })
            .AddOtlpExporter(otlp =>
            {
                var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";
                otlp.Endpoint = new Uri(endpoint);
                otlp.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            });
    })
    .WithMetrics(meter =>
    {
        meter
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            
            .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
            .AddOtlpExporter(otlp =>
            {
                var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";
                otlp.Endpoint = new Uri(endpoint);
                otlp.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            });
    });

var app = builder.Build();

// gRPC summary (method + status)
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try { await next(); }
    finally
    {
        sw.Stop();
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GrpcSummary");
        var method = ctx.Request.Path.Value ?? "";
        using (log.BeginScope(new Dictionary<string, object?>
        {
            ["GrpcMethod"] = method,
            ["StatusCode"] = ctx.Response.StatusCode
        }))
        {
            log.LogInformation("gRPC {GrpcMethod} -> {StatusCode} in {ElapsedMs} ms",
                               method, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
        }
    }
});

// Configure the HTTP request pipeline.
app.UseMigration();
app.MapGrpcService<DiscountService>();
app.MapHealthChecks("/health");
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();





