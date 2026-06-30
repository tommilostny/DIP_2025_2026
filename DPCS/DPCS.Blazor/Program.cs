using DPCS.Blazor.Components;
using DPCS.DAL;
using DPCS.ServiceDefaults;

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

builder.Services.AddSingleton<WordlistService>();

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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Services.EnsureDpcsDbCreated();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
Proto.Log.SetLoggerFactory(loggerFactory);

app.Run();
