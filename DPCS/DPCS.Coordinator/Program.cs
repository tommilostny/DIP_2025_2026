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
