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

agentMeter.CreateObservableGauge("dpcs.agent.gpu.device.hashrate", () =>
	hashcatWrapper.GpuDevices
		.Where(gpu => gpu.CurrentHashrate >= 0)
		.Select(gpu => new Measurement<long>(
			gpu.CurrentHashrate,
			new KeyValuePair<string, object?>("gpu_index", gpu.DeviceIndex),
			new KeyValuePair<string, object?>("gpu_name", string.IsNullOrWhiteSpace(gpu.DeviceName) ? $"GPU {gpu.DeviceIndex}" : gpu.DeviceName))),
	"H/s",
	"Per-device cracking speed from hashcat status telemetry.");

agentMeter.CreateObservableGauge("dpcs.agent.gpu.device.temperature", () =>
	hashcatWrapper.GpuDevices
		.Where(gpu => gpu.Temperature >= 0)
		.Select(gpu => new Measurement<int>(
			gpu.Temperature,
			new KeyValuePair<string, object?>("gpu_index", gpu.DeviceIndex),
			new KeyValuePair<string, object?>("gpu_name", string.IsNullOrWhiteSpace(gpu.DeviceName) ? $"GPU {gpu.DeviceIndex}" : gpu.DeviceName))),
	"C",
	"Per-device temperature from hashcat status telemetry.");

agentMeter.CreateObservableGauge("dpcs.agent.gpu.device.utilization", () =>
	hashcatWrapper.GpuDevices
		.Where(gpu => gpu.GpuUtilization >= 0)
		.Select(gpu => new Measurement<int>(
			gpu.GpuUtilization,
			new KeyValuePair<string, object?>("gpu_index", gpu.DeviceIndex),
			new KeyValuePair<string, object?>("gpu_name", string.IsNullOrWhiteSpace(gpu.DeviceName) ? $"GPU {gpu.DeviceIndex}" : gpu.DeviceName))),
	"%",
	"Per-device utilization from hashcat status telemetry.");

agentMeter.CreateObservableGauge("dpcs.agent.gpu.device.vram.used", () =>
	hashcatWrapper.GpuDevices
		.Where(gpu => gpu.VramUsedBytes >= 0)
		.Select(gpu => new Measurement<long>(
			gpu.VramUsedBytes,
			new KeyValuePair<string, object?>("gpu_index", gpu.DeviceIndex),
			new KeyValuePair<string, object?>("gpu_name", string.IsNullOrWhiteSpace(gpu.DeviceName) ? $"GPU {gpu.DeviceIndex}" : gpu.DeviceName))),
	"By",
	"Per-device used VRAM from hashcat status telemetry.");

agentMeter.CreateObservableGauge("dpcs.agent.gpu.device.vram.total", () =>
	hashcatWrapper.GpuDevices
		.Where(gpu => gpu.VramTotalBytes > 0)
		.Select(gpu => new Measurement<long>(
			gpu.VramTotalBytes,
			new KeyValuePair<string, object?>("gpu_index", gpu.DeviceIndex),
			new KeyValuePair<string, object?>("gpu_name", string.IsNullOrWhiteSpace(gpu.DeviceName) ? $"GPU {gpu.DeviceIndex}" : gpu.DeviceName))),
	"By",
	"Per-device total VRAM from hashcat status telemetry.");

builder.Services.AddActorSystem();
builder.Services.AddHostedService<ActorSystemClusterHostedService>();
builder.Services.AddHostedService<AgentService>();

var host = builder.Build();
await host.RunAsync();