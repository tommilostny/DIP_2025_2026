using DPCS.Coordinator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.CommandLine;

Option<string> hashcatPathOption = new("--hashcat-path", "-p")
{
    Description = "Path to the hashcat executable (optional, defaults to 'hashcat' in system PATH)",
    DefaultValueFactory = _ => "hashcat"
};
Option<string> consulPathOption = new("--consul-path", "-c")
{
    Description = "Path to the Consul executable (optional, defaults to 'consul' in system PATH)",
    DefaultValueFactory = _ => "consul"
};
Option<string?> serverIpOption = new("--server-ip", "-s")
{
    Description = "IP address of the DPCS Coordinator server."
}; 
Option<string?> hostOption = new("--host", "-ip")
{
    Description = "Host IP address for Consul server (optional, auto-detected if not provided)"
};
Option<int> portOption = new("--port", "-pt")
{
    Description = "Port for Proto.Actor (optional, defaults to 0 for dynamic)",
    DefaultValueFactory = _ => 0
};
Option<int> chunkTimeOption = new("--chunk-time", "-ct")
{
    Description = "Target time in seconds for each work chunk assigned to agents (optional)",
    DefaultValueFactory = _ => 60
};
Option<bool> noConsulOption = new("--no-consul", "-nc")
{
    Description = "Do not start the local Consul agent automatically"
};

RootCommand rootCommand = new("Distributed Password Cracking System - Coordinator Node");
rootCommand.Options.Add(hashcatPathOption);
rootCommand.Options.Add(consulPathOption);
rootCommand.Options.Add(serverIpOption);
rootCommand.Options.Add(hostOption);
rootCommand.Options.Add(portOption);
rootCommand.Options.Add(chunkTimeOption);
rootCommand.Options.Add(noConsulOption);

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

var hashcatPath = parseResult.GetValue(hashcatPathOption);
if (string.IsNullOrWhiteSpace(hashcatPath))
{
    Console.Error.WriteLine("Error: Hashcat path cannot be empty. Please provide a valid path or ensure 'hashcat' is in the system PATH.");
    return;
}

var consulPath = parseResult.GetValue(consulPathOption);
var serverIp = parseResult.GetValue(serverIpOption);
var noConsul = parseResult.GetValue(noConsulOption);
if (!noConsul && string.IsNullOrWhiteSpace(serverIp))
{
    Console.Error.WriteLine("Error: Server IP is required. Please provide it using --server-ip/-s.");
    return;
}
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
var chunkTime = parseResult.GetValue(chunkTimeOption);

System.Diagnostics.Process? consulProcess = null;
if (!noConsul)
{
    try
    {
        consulProcess = ConsulWrapper.StartConsulAgent(consulPath ?? "consul", hostIp, serverIp!);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error starting Consul: {ex.Message}");
        return;
    }
}

try
{
    var host = Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((context, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                { "ProtoActor:Consul", ConsulWrapper.ConsulAddress },
                { "ProtoActor:Host", hostIp },
                { "ProtoActor:Port", port.ToString() },
                { "DPCS:ChunkTimeSeconds", chunkTime.ToString() }
            };
            config.AddInMemoryCollection(settings);
        })
        .ConfigureServices((context, services) =>
        {
            services.AddDbContextFactory<DpcsDbContext>(options =>
                options.UseSqlite("Data Source=dpcs_coordinator.db")
            );

            services.AddSingleton(new HashcatWrapper(hashcatPath));
            
            services.AddActorSystem();
            services.AddHostedService<ActorSystemClusterHostedService>();
        })
        .Build();

    using (var scope = host.Services.CreateScope())
    {
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DpcsDbContext>>();
        using var dbContext = dbContextFactory.CreateDbContext();
        dbContext.Database.EnsureCreated();
    }

    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    Proto.Log.SetLoggerFactory(loggerFactory);

    await host.RunAsync();
}
finally
{
    if (consulProcess != null && !consulProcess.HasExited)
    {
        Console.WriteLine("Stopping Consul...");
        consulProcess.Kill();
    }
}



/*
var app = builder.Build();

// Ensure the database and its schema are created before the app starts handling requests/messages.
using (var scope = app.Services.CreateScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DpcsDbContext>>();
    using var dbContext = dbContextFactory.CreateDbContext();
    dbContext.Database.EnsureCreated();
}
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
*/