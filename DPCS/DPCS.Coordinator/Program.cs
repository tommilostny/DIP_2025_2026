using DPCS.Coordinator;

var builder = WebApplication.CreateBuilder(args);

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

app.Run();
