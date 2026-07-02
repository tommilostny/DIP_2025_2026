using DPCS.Coordinator;
using DPCS.DAL;
using DPCS.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Proto.OpenTelemetry;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

//builder.Services.AddOpenTelemetry()
//    .WithMetrics(metrics =>
//    {
//        metrics.AddProtoActorInstrumentation();
//    });

// Aspire injects the connection string named "postgres" from the AppHost
builder.Services.AddDpcsDbContextFactory(builder.Configuration.GetConnectionString("dpcs")
                                         ?? builder.Configuration.GetConnectionString("postgres")
                                         ?? builder.Configuration["DPCS_URI"]
                                         ?? "Host=localhost;Port=5432;Database=dpcs;Username=postgres;Password=password123");

var hashcatPath = builder.Configuration["Hashcat:Path"] ?? "hashcat";
builder.Services.AddSingleton<IHashcatWrapper>(new HashcatWrapper(hashcatPath));
    
builder.Services.AddActorSystem();
builder.Services.AddHostedService<ActorSystemClusterHostedService>();

var host = builder.Build();

host.Services.EnsureDpcsDbCreated();

var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
Proto.Log.SetLoggerFactory(loggerFactory);

await host.RunAsync();