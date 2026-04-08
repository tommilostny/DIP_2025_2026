using DPCS.Blazor.Components;
using DPCS.DAL;
using System.CommandLine;

Option<string> consulPathOption = new("--consul-path", "-c")
{
    Description = "Path to the Consul executable (optional, defaults to 'consul' in system PATH)",
    DefaultValueFactory = _ => "consul"
};
Option<string?> hostOption = new("--host", "-ip")
{
    Description = "Host IP address for Consul server (optional, auto-detected if not provided)"
};
Option<int> portOption = new("--port", "-pt")
{
    Description = "Port for Consul server (optional)",
    DefaultValueFactory = _ => 8000
};

RootCommand rootCommand = new("Distributed Password Cracking System - Coordinator Node");
rootCommand.Options.Add(consulPathOption);
rootCommand.Options.Add(hostOption);
rootCommand.Options.Add(portOption);

if (args.Contains("--help") || args.Contains("-h"))
{
    rootCommand.Parse("-h").Invoke();
    return;
}

ParseResult parseResult = rootCommand.Parse(args);
if (parseResult.Errors.Count > 0)
{
    Console.Error.WriteLine("Error parsing command-line arguments:");
    foreach (var error in parseResult.Errors)
    {
        Console.Error.WriteLine($"  {error.Message}");
    }
    return;
}

var consulPath = parseResult.GetValue(consulPathOption);
var hostIp = parseResult.GetValue(hostOption);
if (string.IsNullOrWhiteSpace(hostIp))
{
    hostIp = ConsulWrapper.GetLocalIpAddress();
    Console.WriteLine($"Auto-detected Host IP: {hostIp}");
}
else
{
    Console.WriteLine($"Using Host IP: {hostIp}");
}
var port = parseResult.GetValue(portOption);

System.Diagnostics.Process? consulProcess = null;
try
{
    consulProcess = ConsulWrapper.StartConsulServer(consulPath ?? "consul", hostIp);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error starting Consul: {ex.Message}");
    return;
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    var settings = new Dictionary<string, string?>
    {
        { "ProtoActor:Consul", ConsulWrapper.ConsulAddress },
        { "ProtoActor:Host", hostIp },
        { "ProtoActor:Port", port.ToString() },
    };
    builder.Configuration.AddInMemoryCollection(settings);

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddDpcsDbContextFactory("Data Source=../DPCS.Coordinator/dpcs_coordinator.db");
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
}
finally
{
    if (consulProcess is { HasExited: false })
    {
        Console.WriteLine("Stopping Consul...");
        consulProcess.Kill();
    }
}
