using DPCS.Coordinator;
using DPCS.Coordinator.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddActorSystem();

builder.Services.AddHostedService<ActorSystemClusterHostedService>();

builder.Services.AddHostedService<JobManagerService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
Proto.Log.SetLoggerFactory(loggerFactory);

app.MapGet("/", () =>
{
    return Task.FromResult("Hello World!");
});

/*
app.MapGet("/smart-bulbs/{identity}", async (ActorSystem actorSystem, string identity) =>
{
    return await actorSystem
        .Cluster()
        .GetSmartBulbGrain(identity)
        .GetState(CancellationToken.None);
});
*/

app.Run();
