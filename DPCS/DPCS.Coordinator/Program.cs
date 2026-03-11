using DPCS.Coordinator;
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
    hostIp = ConsulHelper.GetLocalIpAddress();
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
    consulProcess = ConsulHelper.StartConsulServer(consulPath ?? "consul", hostIp);
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
        { "ProtoActor:Consul", ConsulHelper.ConsulAddress },
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

    app.MapPost("/submit-job/mask", async (MaskJobSpecsModel model, ActorSystem actorSystem) =>
    {
        var request = new HashcatMaskJobSpecs
        {
            Hashes = { model.Hashes },
            Mask = model.Mask,
            MinLength = model.MinLength,
            MaxLength = model.MaxLength,
            HashType = model.HashType
        };
        var jobManager = actorSystem.Cluster().GetJobManagerGrain("root");
        var job = await jobManager.MaskJobSubmission(request, CancellationToken.None);

        return Results.Ok(job);
    })
    .AddEndpointFilter(ModelValidationHelper.ValidationFilter);

    app.MapPost("/submit-job/dictionary", async (DictionaryJobSpecsModel model, ActorSystem actorSystem) =>
    {
        var request = new HashcatDictionaryJobSpecs
        {
            Hashes = { model.Hashes },
            Wordlists = { model.Wordlists },
            HashType = model.HashType
        };
        var jobManager = actorSystem.Cluster().GetJobManagerGrain("root");
        var job = await jobManager.DictionaryJobSubmission(request, CancellationToken.None);

        return Results.Ok(job);
    })
    .AddEndpointFilter(ModelValidationHelper.ValidationFilter);

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