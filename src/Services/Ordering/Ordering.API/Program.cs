using Ordering.API;
using Ordering.Application;
using Ordering.Infrastructure;
using Ordering.Infrastructure.Data.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using System.Reflection;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services
    .AddApplicationServices(builder.Configuration)
    .AddInfrastructureServices(builder.Configuration)
    .AddApiServices(builder.Configuration);

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

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);


// OpenTelemetry tracing
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
            .AddHttpClientInstrumentation(options =>
            {
                options.RecordException = true;
                options.EnrichWithHttpRequestMessage = (activity, req) =>
                {
                    activity.SetTag("peer.hostname", req.RequestUri?.Host);
                    activity.SetTag("http.request_content_length", req.Content?.Headers?.ContentLength);
                };
                options.EnrichWithHttpResponseMessage = (activity, resp) =>
                {
                    activity.SetTag("http.response_content_length", resp.Content?.Headers?.ContentLength);
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
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            
            .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http")
            .AddOtlpExporter(otlp =>
            {
                var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";
                otlp.Endpoint = new Uri(endpoint);
                otlp.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            });
    });

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try { await next(); }
    finally
    {
        sw.Stop();
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HttpSummary");
        using (log.BeginScope(new Dictionary<string, object?>
        {
            ["Route"] = ctx.GetEndpoint()?.DisplayName ?? ctx.Request.Path.Value,
            ["Method"] = ctx.Request.Method,
            ["StatusCode"] = ctx.Response.StatusCode
        }))
        {
            log.LogInformation("HTTP {Method} {Route} -> {StatusCode} in {ElapsedMs} ms",
                               ctx.Request.Method,
                               ctx.GetEndpoint()?.DisplayName ?? ctx.Request.Path.Value,
                               ctx.Response.StatusCode,
                               sw.ElapsedMilliseconds);
        }
    }
});

app.MapControllers();

// Configure the HTTP request pipeline.
app.UseApiServices();

if (app.Environment.IsDevelopment())
{
    await app.InitialiseDatabaseAsync();
}

app.Run();






