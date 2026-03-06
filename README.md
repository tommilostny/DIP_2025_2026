# Distributed Password-Cracking System Using the Actor Model (.NET)

## Master's thesis

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

## Tools

### Proto.Actor

On Linux, the Protobuf compiler may be necessary to install to be able to build the agent and coordinator projects. It can be installed with:

```bash
sudo apt update && sudo apt install -y protobuf-compiler
```

### Hashcat

On Linux install with:

```bash
sudo apt update && sudo apt install hashcat
```

Or download binaries here: [https://hashcat.net/hashcat/](https://hashcat.net/hashcat/), and move the executable to where it is include in the PATH environment variable or provide the path to the executable in the ``--hashcat-path``/``-p`` argument.

### HashiCorp Consul

Install on Linux dependency with:

```bash
wget -O - https://apt.releases.hashicorp.com/gpg | sudo gpg --dearmor -o /usr/share/keyrings/hashicorp-archive-keyring.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/hashicorp-archive-keyring.gpg] https://apt.releases.hashicorp.com $(grep -oP '(?<=UBUNTU_CODENAME=).*' /etc/os-release || lsb_release -cs) main" | sudo tee /etc/apt/sources.list.d/hashicorp.list
sudo apt update && sudo apt install consul
```

Windows:

```powershell
winget install --id Hashicorp.Consul
```

Or more variants at [https://developer.hashicorp.com/consul/install](https://developer.hashicorp.com/consul/install)

Then run server on the coordinator node:

```bash
consul agent -server -bootstrap -data-dir "/tmp/consul" -client '0.0.0.0' -bind '<SERVER_IP>'
```

And on the agent node:

```bash
consul agent -data-dir 'C:\Windows\Temp\ConsulAgentData' -join '<SERVER_IP>' -bind '<AGENT_IP>'
```

---

## System Architecture

### Coordinator Actors

### Agent Actors

### User Interface
