# Distributed Password-Cracking System Using the Actor Model (.NET)

**Author**: Tomáš Milostný, Bc.

**Supervisor**: Jan Pluskal, Ing., Ph.D.

---

## Assignment

1. **Study the problem and technologies.**
Research password-cracking principles and Hashcat capabilities, focusing on workload distribution and GPU resource management. Study the actor model in .NET (e.g., Akka.NET, Orleans) and approaches for reliable distributed computation.

1. **Design the system architecture.**
Propose an actor-based architecture with a Coordinator managing jobs, work distribution, and result aggregation, and Agent nodes running Hashcat instances. Define message flow, checkpointing, error recovery, and communication protocols to ensure low latency and reliability.

1. **Implement coordinator and agents in .NET.**
Implement a Coordinator service for job submission, progress tracking, and result collection. Develop Agent actors that wrap and control local Hashcat processes, handle task execution, and report results back to the Coordinator.

1. **Develop the Hashcat runner.**
Create a robust .NET wrapper around Hashcat, capable of launching tasks, managing keyspaces, parsing outputs, and handling retries or restarts. Support multiple hash modes and enforce proper GPU utilization and isolation.

1. **Deploy and test the distributed system.**
Set up a test environment with multiple nodes (physical or virtual) and GPUs. Automate deployment, configuration, and orchestration of Hashcat agents. Use synthetic hash datasets for evaluation.

1. **Evaluate performance and reliability.**
Measure throughput, scalability, task distribution latency, and fault tolerance. Analyze system behavior under failures and varying workloads, and summarize performance trade-offs and limitations.

---

## Abstract

This thesis addresses the growing computational demands of password recovery by proposing and implementing a distributed orchestration system based on the Actor Model on the .NET platform in C\#. The system combines the Hashcat engine with the Proto.Actor framework to provide a scalable and fault-tolerant control plane for coordinating work across multiple nodes. To support heterogeneous clusters, it uses dynamic chunk sizing based on hardware benchmarking, which helps reduce the impact of slower nodes. An asynchronous prefetch queue is also used to hide network latency and improve GPU utilization. The design was evaluated through both automated in-process simulation and laboratory tests on a cluster of workstations equipped with real GPU hardware. The results indicate that the actor-based approach can effectively utilize available compute resources, recover from node failures, and maintain reliable keyspace coverage, providing a practical foundation for distributed cryptographic operations.

---

## Deployment and Application Startup

The current implementation is organized around two practical startup modes. The first is intended for local development and interactive testing, while the second is intended for deployment on an actual local network where the Manager, Coordinator, and Agent nodes are spread across different machines.

### Local development with .NET Aspire

For local development, the preferred entry point is the Aspire AppHost project. Docker needs to be running in the background before launching. The command below starts the full distributed application from a single location:

```bash
dotnet run --project DPCS.AppHost
```

The AppHost project is responsible for defining the distributed application topology. In the current implementation, it creates a PostgreSQL database resource, adds a Consul container, and then registers the Blazor, Coordinator, and Agent projects as application resources that depend on those infrastructure services. The AppHost uses the `WithReference` mechanism to provide the dependent projects with the connection string for PostgreSQL and the endpoint for the Consul container so that the applications can discover the required infrastructure without hard-coded wiring.

When the application is launched, Aspire also opens a dashboard that shows the running containers and projects, their health status, exposed endpoints, and log output. This makes it convenient to observe the whole system as one deployment unit during development. During development, the PostgreSQL container can take longer to start and initialize the database, causing the Blazor and Coordinator executables to crash because EF Core cannot run the database migration process properly. When that happens, they can be started again from the Aspire dashboard directly.

The configuration files used by the projects are still read from the standard .NET configuration pipeline. The AppHost itself does not require any custom command-line arguments for the normal development flow. The relevant values are supplied through `appsettings.json` files or through environment variables and the Aspire injection mechanism.

Example configuration values used by the different application components:

```json
// DPCS.Blazor/appsettings.json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ProtoActor": {
    "Host": "10.10.10.118",
    "Port": 12000
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5065"
      }
    }
  }
}
```

```json
// DPCS.Coordinator/appsettings.json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Hashcat": {
    "Path": "C:\\hashcat-7.1.2\\hashcat.exe"
  },
  "ConnectionStrings": {
    "dpcs": "Host=10.10.10.118;Port=5432;Database=dpcs;Username=postgres;Password=password123",
    "consul-http": "http://10.10.10.118:8500"
  },
  "ProtoActor": {
    "Host": "10.10.10.118",
    "Port": 12001
  },
  "DPCS": {
    "ServerBaseUrl": "http://10.10.10.118:5065"
  }
}
```

```json
// DPCS.Agent/appsettings.json
{
  "Hashcat": {
    "Path": "C:\\hashcat-7.1.2\\hashcat.exe",
    "WorkloadProfile": 2
  },
  "ConnectionStrings": {
    "consul-http": "http://10.10.10.118:8500"
  },
  "ProtoActor": {
    "Host": "10.10.10.103",
    "Port": 0
  }
}
```

For the local setup, the most important values are the Proto.Actor host and port, the Hashcat executable path, and the web server base URL used by the Coordinator. In a development environment, they are often left at loopback values (`127.0.0.1`), but the same configuration can be adapted to a private LAN by providing the actual IP address of the relevant machine.

### Deployment on a real local network

When the system is deployed on a real local network, the Manager, Coordinator, and Agent nodes are started as ordinary .NET applications. In this case, only the workstation hosting the Manager node needs to have Docker running in the background, as it is responsible for launching the Consul and PostgreSQL containers. To start them, run the following command in the directory with the provided `docker-compose.yml` file:

```bash
docker-compose up -d
```

Then, all nodes are launched in the same manner in separate terminal windows:

```bash
dotnet run --project DPCS.Blazor
dotnet run --project DPCS.Coordinator
dotnet run --project DPCS.Agent
```

For environments where the Agent is expected to run on different machines, the Agent project can also be published and started from the generated output directory. This approach is suitable for laboratory-style deployment where the published output directory is copied onto each workstation's disk. The publish step is shown below:

```bash
dotnet publish ./DPCS.Agent/DPCS.Agent.csproj -c Release
```

After publishing, the executable can be launched directly from the published directory as follows:

```bash
dotnet DPCS.Agent.dll
```

In this deployment mode, the configuration values must be adjusted to match the actual network topology. The Manager node should expose its web UI using the `0.0.0.0` IP address and host the Consul service that the other nodes use for discovery by using its own IP address. The Coordinator and Agent projects should point to the Manager node's address through the relevant configuration values, while each node should also publish its own reachable Proto.Actor host address and the port value (where `0` allows the operating system to select a dynamic port, and a fixed value can be used when a specific port must be reserved), the path to the Hashcat executable, and, where necessary, the web server base URL used by the Coordinator to reach the Manager UI. This arrangement allows the nodes to join the same cluster even when they are located on different machines in the same private network.

### Tools and prerequisites

#### Proto.Actor

On Linux, the Protobuf compiler may be necessary to install to be able to build the agent and coordinator projects. It can be installed with:

```bash
sudo apt update && sudo apt install -y protobuf-compiler
```

#### Hashcat

On Linux install with:

```bash
sudo apt update && sudo apt install hashcat
```

Or download binaries here: [https://hashcat.net/hashcat/](https://hashcat.net/hashcat/), and move the executable to where it is included in the PATH environment variable or provide the path to the executable in the ``Hashcat:Path`` field in the ``appsettings.json`` configuration file.

### Technology stack and versions

| Component | Version / usage |
| --- | --- |
| .NET target framework | .NET 10 (`net10.0`) |
| ASP.NET Core / Blazor Server | Included with .NET 10 |
| .NET Aspire | 13.4.6 |
| Aspire AppHost / hosting | 13.4.6 |
| Aspire PostgreSQL integration | 13.4.6 |
| Proto.Actor | 1.8.0 |
| Proto.Cluster | 1.8.0 |
| Proto.Cluster.Consul | 1.8.0 |
| Proto.Remote | 1.8.0 |
| gRPC tools / Protobuf support | 2.81.1 |
| OpenTelemetry Auto-Instrumentation | 1.15.0 |
| Consul | 1.15.4 (containerized for discovery) |
| PostgreSQL | 18.3 (containerized for database) |
| Hashcat | 7.1.2 |
| UI plotting library | ScottPlot 5.1.59 |
