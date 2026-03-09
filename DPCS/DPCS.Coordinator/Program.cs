using DPCS.Coordinator;
using System.CommandLine;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

Option<string> consulPathOption = new("--consul-path", "-c")
{
    Description = "Path to the consul executable (optional, defaults to 'consul' in system PATH)",
    DefaultValueFactory = _ => "consul"
};
Option<string?> hostOption = new("--host", "-ip")
{
    Description = "Host IP address for Proto.Actor (optional, auto-detected if not provided)"
};
Option<int> portOption = new("--port", "-pt")
{
    Description = "Port for Proto.Actor (optional)",
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
    hostIp = GetLocalIpAddress();
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
    consulProcess = StartConsul(consulPath ?? "consul", hostIp);
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
        { "ProtoActor:Consul", "http://localhost:8500" },
        { "ProtoActor:Host", hostIp },
        { "ProtoActor:Port", port.ToString() }
    };
    builder.Configuration.AddInMemoryCollection(settings);

    // Add services to the container.
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    builder.Services.AddActorSystem();

    builder.Services.AddHostedService<ActorSystemClusterHostedService>();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "DPCS Coordinator API v1");
        });
    }

    app.UseHttpsRedirection();

    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    Proto.Log.SetLoggerFactory(loggerFactory);

    app.MapPost("/submit-job/mask", async (HashcatMaskJobSpecs request, ActorSystem actorSystem) =>
    {
        var jobManager = actorSystem.Cluster().GetJobManagerGrain("root");
        var job = await jobManager.MaskJobSubmission(request, CancellationToken.None);

        return Results.Ok(job);
    });

    app.MapPost("/submit-job/dictionary", async (HashcatDictionaryJobSpecs request, ActorSystem actorSystem) =>
    {
        var jobManager = actorSystem.Cluster().GetJobManagerGrain("root");
        var job = await jobManager.DictionaryJobSubmission(request, CancellationToken.None);

        return Results.Ok(job);
    });

    app.MapGet("/job-status/{jobId}", async (string jobId, ActorSystem actorSystem) =>
    {
        if (!JobIdSecurity.ValidateSignedId(jobId))
        {
            return Results.NotFound($"Job with id {jobId} not found (invalid signature)");
        }

        var jobCoordinator = actorSystem.Cluster().GetJobCoordinatorGrain(jobId);
        var jobStatus = await jobCoordinator.GetJobStatus(CancellationToken.None);
        if (jobStatus is null or { Status: "NotFound" })
        {
            return Results.NotFound($"Job with id {jobId} not found");
        }
        return Results.Ok(jobStatus);
    });

    app.MapDelete("/cancel-job/{jobId}", async (string jobId, ActorSystem actorSystem) =>
    {
        if (!JobIdSecurity.ValidateSignedId(jobId))
        {
            return Results.NotFound($"Job with id {jobId} not found (invalid signature)");
        }

        var jobManager = actorSystem.Cluster().GetJobManagerGrain("root");
        await jobManager.CancelJob(new JobId { Id = jobId }, CancellationToken.None);
        return Results.Ok();
    });

    app.Run();
}
finally
{
    if (consulProcess != null && !consulProcess.HasExited)
    {
        Console.WriteLine("Stopping Consul...");
        consulProcess.Kill();
    }
}

static string GetLocalIpAddress()
{
    return NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString())
        .FirstOrDefault() ?? "127.0.0.1";
}

static System.Diagnostics.Process StartConsul(string consulPath, string hostIp)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = consulPath,
        Arguments = $"agent -server -bootstrap -data-dir ./.consul -bind {hostIp} -client 0.0.0.0",
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    var process = System.Diagnostics.Process.Start(startInfo) ?? throw new Exception("Failed to start Consul process.");
    if (process.WaitForExit(500)) throw new Exception($"Consul exited with code {process.ExitCode}");
    Console.WriteLine("Consul started.");
    return process;
}
