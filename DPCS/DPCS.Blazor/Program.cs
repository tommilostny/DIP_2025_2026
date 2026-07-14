using DPCS.Blazor.Components;
using DPCS.DAL;
using DPCS.ServiceDefaults;
using Proto.OpenTelemetry;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// This extension method adds service discovery, telemetry, and health checks.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Aspire injects the connection string named "postgres" from the AppHost
builder.Services.AddDpcsDbContextFactory(builder.Configuration.GetConnectionString("dpcs")
                                         ?? builder.Configuration.GetConnectionString("postgres")
                                         ?? builder.Configuration["DPCS_URI"]
                                         ?? "Host=localhost;Port=5432;Database=dpcs;Username=postgres;Password=password123");
builder.Services.AddActorSystem();
builder.Services.AddHostedService<ActorSystemClusterHostedService>();

builder.Services.AddSingleton<MaskService>();
builder.Services.AddSingleton<WordlistService>();
builder.Services.AddSingleton<JobReportExportService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAntiforgery();

app.MapGet("/api/wordlists/{fileName}/checksum", async (
    string fileName,
    long startByte,
    long endByte,
    WordlistService wordlistService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var checksum = await wordlistService.GetWordlistRangeChecksumAsync(fileName, startByte, endByte, cancellationToken);
        return Results.Text(checksum, "text/plain");
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/api/jobs/{jobId}/work-units", async (
    string jobId,
    string? format,
    string? outcome,
    long? fromUnixMs,
    long? toUnixMs,
    ActorSystem actorSystem,
    JobReportExportService reportExportService,
    CancellationToken cancellationToken) =>
{
    var filter = new WorkUnitLifecycleFilter
    {
        Outcome = outcome ?? string.Empty,
        FromUnixMs = fromUnixMs ?? 0,
        ToUnixMs = toUnixMs ?? 0
    };

    var lifecycle = await actorSystem
        .Cluster()
        .GetJobCoordinatorGrain(jobId)
        .GetWorkUnitLifecycle(filter, cancellationToken)
        ?? new WorkUnitLifecycleExport { JobId = jobId };

    if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(lifecycle);
    }

    var csv = reportExportService.BuildLifecycleCsv(lifecycle);
    return Results.Text(csv, "text/csv", Encoding.UTF8);
});

app.MapGet("/api/jobs/{jobId}/report-chart", async (
    string jobId,
    string? type,
    ActorSystem actorSystem,
    JobReportExportService reportExportService,
    CancellationToken cancellationToken) =>
{
    var coordinator = actorSystem.Cluster().GetJobCoordinatorGrain(jobId);

    var lifecycle = await coordinator
        .GetWorkUnitLifecycle(new WorkUnitLifecycleFilter(), cancellationToken)
        ?? new WorkUnitLifecycleExport { JobId = jobId };

    var telemetry = await coordinator
        .GetAgentGpuTelemetryHistory(cancellationToken)
        ?? new AgentGpuTelemetryExport { JobId = jobId };

    var png = reportExportService.BuildReportChartPng(lifecycle, telemetry, type);
    return Results.File(png, "image/png");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Services.EnsureDpcsDbCreated();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
Proto.Log.SetLoggerFactory(loggerFactory);

app.Run();
