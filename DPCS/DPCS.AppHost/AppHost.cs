var builder = DistributedApplication.CreateBuilder(args);

#if DEBUG
// --- OPTION 1: Use a container for local development (default) ---
var postgres = builder.AddPostgres("postgres").WithPgAdmin();
var database = postgres.AddDatabase("dpcs");
#else
// --- OPTION 2: Connect to an existing database server ---
// This defines a connection string resource named "postgres".
var database = builder.AddConnectionString("postgres");
#endif

// Add a container for Consul, which provides service discovery for Proto.Actor
var consul = builder.AddContainer("consul", "consul", "1.15.4")
                    .WithHttpEndpoint(targetPort: 8500, name: "consul-http");

// Add the Blazor UI project, injecting references to the database and Consul
var blazor = builder.AddProject<Projects.DPCS_Blazor>("dpcs-blazor")
    .WithReference(database)
    .WithReference(consul.GetEndpoint("consul-http")) // Reference the specific Consul HTTP endpoint
    .WithExternalHttpEndpoints(); // Makes the UI accessible from your local network

// Add the Coordinator project, also with references to the database and Consul
var coordinator = builder.AddProject<Projects.DPCS_Coordinator>("dpcs-coordinator")
                         .WithReference(database)
                         .WithReference(consul.GetEndpoint("consul-http"));

// Add the Agent project, with a reference to Consul.
// The 'WithReplicas(3)' will launch 3 instances of the agent for testing.
builder.AddProject<Projects.DPCS_Agent>("dpcs-agent")
       .WithReference(consul.GetEndpoint("consul-http"));

builder.Build().Run();
