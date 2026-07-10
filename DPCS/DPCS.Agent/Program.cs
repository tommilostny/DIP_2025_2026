﻿using DPCS.Agent;
using DPCS.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Proto.OpenTelemetry;
using System.Diagnostics.Metrics;

var builder = Host.CreateApplicationBuilder(args);

// This extension method adds service discovery, telemetry, and health checks.
builder.AddServiceDefaults();

//builder.Services.AddOpenTelemetry()
//    .WithMetrics(metrics =>
//    {
//        metrics.AddProtoActorInstrumentation();
//    });

// Aspire can inject these from appsettings.json or the AppHost launch profile
var hashcatPath = builder.Configuration["Hashcat:Path"] ?? "hashcat";
var workloadProfile = builder.Configuration.GetValue<int?>("Hashcat:WorkloadProfile") ?? 2;

var hashcatWrapper = new HashcatWrapper(hashcatPath, workloadProfile);
builder.Services.AddSingleton<IHashcatWrapper>(hashcatWrapper);

// Define a new Meter for our custom agent metrics
var agentMeter = new Meter("DPCS.Agent");

// Create observable gauges that will automatically poll the HashcatWrapper for its current state
agentMeter.CreateObservableGauge("dpcs.agent.hashrate", () => hashcatWrapper.CurrentHashrate, "H/s", "The current cracking speed of the agent.");
agentMeter.CreateObservableGauge("dpcs.agent.gpu.temperature", () => hashcatWrapper.Temperature, "C", "The average temperature of the GPU devices.");
agentMeter.CreateObservableGauge("dpcs.agent.gpu.fanspeed", () => hashcatWrapper.FanSpeed, "%", "The average fan speed of the GPU devices.");
agentMeter.CreateObservableGauge("dpcs.agent.gpu.utilization", () => hashcatWrapper.GpuUtilization, "%", "The average utilization of the GPU devices.");
agentMeter.CreateObservableGauge("dpcs.agent.reject_rate", () => hashcatWrapper.RejectRate, "%", "The percentage of rejected shares.");

builder.Services.AddActorSystem();
builder.Services.AddHostedService<ActorSystemClusterHostedService>();
builder.Services.AddHostedService<AgentService>();

var host = builder.Build();
await host.RunAsync();