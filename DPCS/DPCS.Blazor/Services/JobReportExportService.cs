using System.Globalization;
using System.Text;
using ScottPlot;

namespace DPCS.Blazor.Services;

public sealed class JobReportExportService(WordlistService wordlistService)
{
    private readonly WordlistService _wordlistService = wordlistService;

    public string BuildLifecycleCsv(WorkUnitLifecycleExport export)
    {
        static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        static string TimestampToIso8601(Google.Protobuf.WellKnownTypes.Timestamp? ts)
            => ts is null ? string.Empty : ts.ToDateTime().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine("request_id,job_id,mode,agent_key,chunk_summary,assigned_at,completed_at,timed_out_at,outcome,processing_duration_seconds,recovered_count");

        foreach (var record in export.Records)
        {
            sb.Append(Escape(record.RequestId)).Append(',')
              .Append(Escape(record.JobId)).Append(',')
              .Append(Escape(record.Mode)).Append(',')
              .Append(Escape(record.AgentKey)).Append(',')
              .Append(Escape(record.ChunkSummary)).Append(',')
              .Append(Escape(TimestampToIso8601(record.AssignedAt))).Append(',')
              .Append(Escape(TimestampToIso8601(record.CompletedAt))).Append(',')
              .Append(Escape(TimestampToIso8601(record.TimedOutAt))).Append(',')
              .Append(Escape(record.Outcome)).Append(',')
              .Append(record.ProcessingDurationSeconds.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(record.RecoveredCount)
              .AppendLine();
        }

        return sb.ToString();
    }

    public byte[] BuildReportChartPng(WorkUnitLifecycleExport export, AgentGpuTelemetryExport? telemetryExport, JobProgressHistoryExport? progressHistory, string? chartType)
    {
        var normalized = string.IsNullOrWhiteSpace(chartType)
            ? "load-balancing"
            : chartType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "load-balancing" => BuildLoadBalancingPng(export),
            "agent-workload" => BuildLoadBalancingPng(export),
            "mean-work-time" => BuildMeanWorkTimePng(export),
            "agent-duration" => BuildMeanWorkTimePng(export),
            "duration-distribution" => BuildDurationDistributionPng(export),
            "gpu-usage-time" => BuildGpuUsageOverTimePng(telemetryExport),
            "agent-gpu-usage-time" => BuildGpuUsageOverTimePng(telemetryExport),
            "gpu-temperature-time" => BuildGpuTemperatureOverTimePng(telemetryExport),
            "agent-gpu-temperature-time" => BuildGpuTemperatureOverTimePng(telemetryExport),
            "hashrate-time" => BuildHashrateOverTimePng(telemetryExport),
            "agent-hashrate-time" => BuildHashrateOverTimePng(telemetryExport),
            "cluster-hashrate-time" => BuildClusterHashrateOverTimePng(telemetryExport),
            "progress-time" => BuildProgressOverTimePng(export, progressHistory, telemetryExport),
            "progress-time-log" => BuildProgressOverTimePng(export, progressHistory, telemetryExport, true),
            "job-progress-time-log" => BuildProgressOverTimePng(export, progressHistory, telemetryExport, true),
            "job-progress-time" => BuildProgressOverTimePng(export, progressHistory, telemetryExport),
            _ => BuildLoadBalancingPng(export)
        };
    }

    private static byte[] BuildGpuTemperatureOverTimePng(AgentGpuTelemetryExport? telemetryExport)
    {
        var plot = new Plot();
        plot.Title("GPU Temperature Over Time (Per Agent + Global Average)");
        plot.XLabel("Time Since First Sample (s)");
        plot.YLabel("Temperature (°C)");

        if (telemetryExport is null || telemetryExport.Samples.Count == 0)
        {
            plot.Add.Text("No GPU telemetry samples available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1400, 800);
        }

        var ordered = telemetryExport.Samples
            .OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue)
            .ToList();

        var baseTime = ordered[0].CapturedAt?.ToDateTime().ToUniversalTime() ?? DateTime.UtcNow;
        var globalMaxX = ordered
            .Select(sample => ((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds)
            .DefaultIfEmpty(0)
            .Max();

        var byAgent = ordered
            .GroupBy(sample => string.IsNullOrWhiteSpace(sample.AgentKey) ? "unknown" : sample.AgentKey)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var group in byAgent)
        {
            var points = group
                .Select(sample => new
                {
                    X = ((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds,
                    Y = ResolveTemperature(sample)
                })
                .Where(point => point.Y >= 0)
                .OrderBy(point => point.X)
                .ToList();

            var lastSample = group
                .OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue)
                .LastOrDefault();
            if (lastSample is not null && !lastSample.IsAlive && points.Count > 0)
            {
                points.Add(new { X = points[^1].X, Y = 0.0 });
            }

            if (points.Count == 0)
            {
                continue;
            }

            var scatter = plot.Add.Scatter(
                points.Select(point => point.X).ToArray(),
                points.Select(point => point.Y).ToArray());
            scatter.LegendText = Truncate(group.Key, 42);
            scatter.LineWidth = 2;
            scatter.MarkerSize = 0;
        }

        var averageBuckets = ordered
            .GroupBy(sample => (long)Math.Floor(((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds))
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                X = (double)group.Key,
                Y = group
                    .GroupBy(sample => string.IsNullOrWhiteSpace(sample.AgentKey) ? "unknown" : sample.AgentKey)
                    .Select(agentGroup => agentGroup.OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue).Last())
                    .Where(sample => sample.IsAlive)
                    .Select(ResolveTemperature)
                    .Where(value => value >= 0)
                    .DefaultIfEmpty()
                    .Average()
            })
            .ToList();

        if (averageBuckets.Count > 0)
        {
            var avgScatter = plot.Add.Scatter(
                averageBuckets.Select(point => point.X).ToArray(),
                averageBuckets.Select(point => point.Y).ToArray());
            avgScatter.LegendText = "All Agents Average";
            avgScatter.Color = Colors.Black;
            avgScatter.LineWidth = 3;
            avgScatter.LinePattern = LinePattern.Dashed;
            avgScatter.MarkerSize = 0;
        }

        plot.Axes.SetLimitsY(0, 110);
        plot.ShowLegend();
        return RenderPlotToPngBytes(plot, 1400, 800);
    }

    private static byte[] BuildHashrateOverTimePng(AgentGpuTelemetryExport? telemetryExport)
    {
        var plot = new Plot();
        plot.Title("Hashrate Over Time (Per Agent + Global Average)");
        plot.XLabel("Time Since First Sample (s)");

        if (telemetryExport is null || telemetryExport.Samples.Count == 0)
        {
            plot.Add.Text("No GPU telemetry samples available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1400, 800);
        }

        var ordered = telemetryExport.Samples
            .OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue)
            .ToList();

        var baseTime = ordered[0].CapturedAt?.ToDateTime().ToUniversalTime() ?? DateTime.UtcNow;
        var globalMaxX = ordered
            .Select(sample => ((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds)
            .DefaultIfEmpty(0)
            .Max();

        var byAgent = ordered
            .GroupBy(sample => string.IsNullOrWhiteSpace(sample.AgentKey) ? "unknown" : sample.AgentKey)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        var chartMaxHashrate = ordered
            .Select(ResolveHashrate)
            .Where(value => value >= 0)
            .DefaultIfEmpty(0)
            .Max();
        var (scale, unitLabel) = ResolveHashrateScale(chartMaxHashrate);
        plot.YLabel($"Hashrate ({unitLabel})");

        foreach (var group in byAgent)
        {
            var points = group
                .Select(sample => new
                {
                    X = ((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds,
                    Y = ResolveHashrate(sample) / scale
                })
                .Where(point => point.Y >= 0)
                .OrderBy(point => point.X)
                .ToList();

            var lastSample = group
                .OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue)
                .LastOrDefault();
            if (lastSample is not null && !lastSample.IsAlive && points.Count > 0)
            {
                points.Add(new { X = points[^1].X, Y = 0.0 });
            }

            if (points.Count == 0)
            {
                continue;
            }

            var scatter = plot.Add.Scatter(
                points.Select(point => point.X).ToArray(),
                points.Select(point => point.Y).ToArray());
            scatter.LegendText = Truncate(group.Key, 42);
            scatter.LineWidth = 2;
            scatter.MarkerSize = 0;
        }

        var averageBuckets = ordered
            .GroupBy(sample => (long)Math.Floor(((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds))
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                X = (double)group.Key,
                Y = group
                    .GroupBy(sample => string.IsNullOrWhiteSpace(sample.AgentKey) ? "unknown" : sample.AgentKey)
                    .Select(agentGroup => agentGroup.OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue).Last())
                    .Where(sample => sample.IsAlive)
                    .Select(sample => ResolveHashrate(sample) / scale)
                    .Where(value => value >= 0)
                    .DefaultIfEmpty()
                    .Average()
            })
            .ToList();

        if (averageBuckets.Count > 0)
        {
            var avgScatter = plot.Add.Scatter(
                averageBuckets.Select(point => point.X).ToArray(),
                averageBuckets.Select(point => point.Y).ToArray());
            avgScatter.LegendText = "All Agents Average";
            avgScatter.Color = Colors.Black;
            avgScatter.LineWidth = 3;
            avgScatter.LinePattern = LinePattern.Dashed;
            avgScatter.MarkerSize = 0;
        }

        plot.ShowLegend();
        return RenderPlotToPngBytes(plot, 1400, 800);
    }

    private static byte[] BuildClusterHashrateOverTimePng(AgentGpuTelemetryExport? telemetryExport)
    {
        var plot = new Plot();
        plot.Title("Cluster Aggregated Hashrate Over Time");
        plot.XLabel("Time Since First Sample (s)");

        if (telemetryExport is null || telemetryExport.Samples.Count == 0)
        {
            plot.Add.Text("No GPU telemetry samples available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1400, 800);
        }

        var ordered = telemetryExport.Samples
            .OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue)
            .ToList();

        var baseTime = ordered[0].CapturedAt?.ToDateTime().ToUniversalTime() ?? DateTime.UtcNow;

        var chartMaxHashrate = ordered
            .Select(ResolveHashrate)
            .Where(value => value >= 0)
            .DefaultIfEmpty(0)
            .Max();
        var (scale, unitLabel) = ResolveHashrateScale(chartMaxHashrate);
        plot.YLabel($"Total Cluster Hashrate ({unitLabel})");

        var clusterBuckets = ordered
            .GroupBy(sample => (long)Math.Floor(((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds))
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                X = (double)group.Key,
                Y = group
                    .GroupBy(sample => string.IsNullOrWhiteSpace(sample.AgentKey) ? "unknown" : sample.AgentKey)
                    .Select(agentGroup => agentGroup.OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue).Last())
                    .Where(sample => sample.IsAlive)
                    .Select(sample => ResolveHashrate(sample) / scale)
                    .Where(value => value >= 0)
                    .Sum()
            })
            .ToList();

        if (clusterBuckets.Count == 0)
        {
            plot.Add.Text("No alive hashrate samples available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1400, 800);
        }

        var clusterScatter = plot.Add.Scatter(
            clusterBuckets.Select(point => point.X).ToArray(),
            clusterBuckets.Select(point => point.Y).ToArray());
        clusterScatter.LegendText = "Cluster Total Hashrate";
        clusterScatter.Color = Colors.DarkGreen;
        clusterScatter.LineWidth = 3;
        clusterScatter.MarkerSize = 0;

        plot.ShowLegend();
        return RenderPlotToPngBytes(plot, 1400, 800);
    }

    private static byte[] BuildProgressOverTimePng(WorkUnitLifecycleExport export, JobProgressHistoryExport? progressHistory, AgentGpuTelemetryExport? telemetryExport, bool compressTime = false)
    {
        var plot = new Plot();
        plot.Title(compressTime ? "Job Progress Over Time (Log Time)" : "Job Progress Over Time");
        plot.XLabel(compressTime ? "Log Time Since First Assignment (log10(s + 1))" : "Time Since First Assignment (s)");
        plot.YLabel("Global Job Progress (%)");

        if (progressHistory is null || progressHistory.Samples.Count == 0)
        {
            plot.Add.Text("No progress samples available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1400, 800);
        }

        var orderedSamples = progressHistory.Samples
            .Where(sample => sample.CapturedAt is not null)
            .OrderBy(sample => sample.CapturedAt!.ToDateTime())
            .ToList();

        var baseTime = orderedSamples[0].CapturedAt!.ToDateTime().ToUniversalTime();

        var xValues = new List<double>();
        var yValues = new List<double>();
        foreach (var sample in orderedSamples)
        {
            var capturedAt = sample.CapturedAt!.ToDateTime().ToUniversalTime();
            xValues.Add(TransformProgressTime((capturedAt - baseTime).TotalSeconds, compressTime));
            yValues.Add(Math.Clamp(sample.ProgressPercentage, 0, 100));
        }

        var maxXForClustering = xValues.Count > 0 ? xValues.Max() : 0;

        var progressScatter = plot.Add.Scatter(xValues.ToArray(), yValues.ToArray());
        progressScatter.LineWidth = 3;
        progressScatter.MarkerSize = 0;
        progressScatter.LegendText = "Progress";

        var disconnectEvents = (telemetryExport?.Samples ?? [])
            .Where(sample => !sample.IsAlive && sample.CapturedAt is not null)
            .GroupBy(sample => sample.AgentKey)
            .SelectMany(group => group
                .OrderBy(sample => sample.CapturedAt!.ToDateTime())
                .Take(1)
                .Select(sample => new
                {
                    X = TransformProgressTime((sample.CapturedAt!.ToDateTime().ToUniversalTime() - baseTime).TotalSeconds, compressTime),
                    Label = string.IsNullOrWhiteSpace(sample.AgentKey) ? "agent" : Truncate(sample.AgentKey, 24)
                }))
            .OrderBy(evt => evt.X)
            .ToList();

        var timeoutEvents = export.Records
            .Where(r => r.TimedOutAt is not null || string.Equals(r.Outcome, "timed_out_requeued", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.TimedOutAt?.ToDateTime().ToUniversalTime() ?? r.AssignedAt?.ToDateTime().ToUniversalTime() ?? baseTime)
            .Select(ts => TransformProgressTime((ts - baseTime).TotalSeconds, compressTime))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var retryEvents = export.Records
            .Where(r => string.Equals(r.Outcome, "timed_out_requeued", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.AssignedAt?.ToDateTime().ToUniversalTime() ?? baseTime)
            .Select(ts => TransformProgressTime((ts - baseTime).TotalSeconds, compressTime))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var allEventMaxX = Math.Max(
            disconnectEvents.Select(e => e.X).DefaultIfEmpty(0).Max(),
            Math.Max(timeoutEvents.DefaultIfEmpty(0).Max(), retryEvents.DefaultIfEmpty(0).Max()));
        maxXForClustering = Math.Max(maxXForClustering, allEventMaxX);

        var clusterWindowSeconds = ResolveClusterWindowSeconds(maxXForClustering);
        var disconnectClusters = BuildEventClusters(disconnectEvents.Select(e => e.X), clusterWindowSeconds);
        var timeoutClusters = BuildEventClusters(timeoutEvents, clusterWindowSeconds);
        var retryClusters = BuildEventClusters(retryEvents, clusterWindowSeconds);

        var maxY = 108.0;
        foreach (var cluster in disconnectClusters)
        {
            var line = plot.Add.VerticalLine(cluster.X);
            line.Color = Colors.Red;
            line.LineWidth = 2;
            line.LinePattern = LinePattern.Dashed;
            line.Text = cluster.Count == 1
                ? "Disconnect"
                : $"Disconnect x{cluster.Count}";
        }

        foreach (var cluster in timeoutClusters)
        {
            var line = plot.Add.VerticalLine(cluster.X);
            line.Color = Colors.Orange;
            line.LineWidth = 1.5f;
            line.LinePattern = LinePattern.DenselyDashed;
            line.Text = cluster.Count == 1
                ? "Chunk timeout"
                : $"Chunk timeout x{cluster.Count}";
        }

        foreach (var cluster in retryClusters)
        {
            var line = plot.Add.VerticalLine(cluster.X);
            line.Color = Colors.Blue;
            line.LineWidth = 1.5f;
            line.LinePattern = LinePattern.Dotted;
            line.Text = cluster.Count == 1
                ? "Chunk retry"
                : $"Chunk retry x{cluster.Count}";
        }

        if (xValues.Count > 0)
        {
            var maxX = Math.Max(xValues.Max(), allEventMaxX);
            plot.Axes.SetLimitsX(0, maxX + 5);
        }

        plot.Axes.SetLimitsY(0, maxY);
        plot.ShowLegend();
        return RenderPlotToPngBytes(plot, 1400, 800);
    }

    private static double TransformProgressTime(double seconds, bool compressTime)
    {
        var safeSeconds = Math.Max(0, seconds);
        return compressTime ? Math.Log10(safeSeconds + 1.0) : safeSeconds;
    }

    private static double ResolveClusterWindowSeconds(double maxX)
    {
        if (maxX <= 0)
        {
            return 2;
        }

        // Target roughly <= 80 markers per event type on very long jobs.
        var adaptive = maxX / 80.0;
        return Math.Clamp(adaptive, 2, 30);
    }

    private static List<EventCluster> BuildEventClusters(IEnumerable<double> points, double windowSeconds)
    {
        var ordered = points
            .Where(x => x >= 0)
            .OrderBy(x => x)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        var clusters = new List<EventCluster>();
        var current = new List<double> { ordered[0] };

        for (var i = 1; i < ordered.Count; i++)
        {
            var point = ordered[i];
            var last = current[^1];
            if (point - last <= windowSeconds)
            {
                current.Add(point);
                continue;
            }

            clusters.Add(new EventCluster(current.Average(), current.Count));
            current = [point];
        }

        clusters.Add(new EventCluster(current.Average(), current.Count));
        return clusters;
    }

    private readonly record struct EventCluster(double X, int Count);

    private static string BuildChunkIdentity(string? mode, string? chunkSummary)
        => $"{(mode ?? string.Empty).Trim().ToLowerInvariant()}|{(chunkSummary ?? string.Empty).Trim()}";

    private static byte[] BuildGpuUsageOverTimePng(AgentGpuTelemetryExport? telemetryExport)
    {
        var plot = new Plot();
        plot.Title("GPU Utilization Over Time (Per Agent + Global Average)");
        plot.XLabel("Time Since First Sample (s)");
        plot.YLabel("GPU Utilization (%)");

        if (telemetryExport is null || telemetryExport.Samples.Count == 0)
        {
            plot.Add.Text("No GPU telemetry samples available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1400, 800);
        }

        var ordered = telemetryExport.Samples
            .OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue)
            .ToList();

        var baseTime = ordered[0].CapturedAt?.ToDateTime().ToUniversalTime() ?? DateTime.UtcNow;

        var byAgent = ordered
            .GroupBy(sample => string.IsNullOrWhiteSpace(sample.AgentKey) ? "unknown" : sample.AgentKey)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        var globalMaxX = ordered
            .Select(sample => ((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds)
            .DefaultIfEmpty(0)
            .Max();

        foreach (var group in byAgent)
        {
            var points = group
                .Select(sample => new
                {
                    X = ((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds,
                    Y = ResolveGpuUtilization(sample)
                })
                .Where(point => point.Y >= 0)
                .OrderBy(point => point.X)
                .ToList();

            var lastSample = group
                .OrderBy(sample => sample.CapturedAt?.ToDateTime() ?? DateTime.MinValue)
                .LastOrDefault();
            if (lastSample is not null && !lastSample.IsAlive && points.Count > 0)
            {
                points.Add(new { X = points[^1].X, Y = 0 });
            }

            if (points.Count == 0)
            {
                continue;
            }

            var scatter = plot.Add.Scatter(
                points.Select(point => point.X).ToArray(),
                points.Select(point => point.Y).ToArray());
            scatter.LegendText = Truncate(group.Key, 42);
            scatter.LineWidth = 2;
            scatter.MarkerSize = 0;
        }

        var buckets = ordered
            .Select(sample => new
            {
                Second = (long)Math.Floor(((sample.CapturedAt?.ToDateTime().ToUniversalTime() ?? baseTime) - baseTime).TotalSeconds),
                Utilization = ResolveGpuUtilization(sample),
                sample.IsAlive
            })
            .Where(item => item.Utilization >= 0 && item.IsAlive)
            .GroupBy(item => item.Second)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                X = (double)group.Key,
                Y = group.Average(item => item.Utilization)
            })
            .ToList();

        if (buckets.Count > 0)
        {
            var avgScatter = plot.Add.Scatter(
                buckets.Select(point => point.X).ToArray(),
                buckets.Select(point => point.Y).ToArray());
            avgScatter.LegendText = "All Agents Average";
            avgScatter.Color = Colors.Black;
            avgScatter.LineWidth = 3;
            avgScatter.LinePattern = LinePattern.Dashed;
            avgScatter.MarkerSize = 0;
        }

        plot.Axes.SetLimitsY(0, 100);
        plot.ShowLegend();
        return RenderPlotToPngBytes(plot, 1400, 800);
    }

    private static int ResolveGpuUtilization(AgentGpuTelemetrySample sample)
    {
        if (sample.GpuDevices.Count > 0)
        {
            var usableDeviceValues = sample.GpuDevices
                .Where(device => device.GpuUtilization >= 0)
                .Select(device => device.GpuUtilization)
                .ToList();

            if (usableDeviceValues.Count > 0)
            {
                return (int)Math.Round(usableDeviceValues.Average());
            }
        }

        return sample.GpuUtilization;
    }

    private static double ResolveTemperature(AgentGpuTelemetrySample sample)
    {
        if (sample.Temperature >= 0)
        {
            return sample.Temperature;
        }

        if (sample.GpuDevices.Count > 0)
        {
            var usable = sample.GpuDevices
                .Where(device => device.Temperature >= 0)
                .Select(device => (double)device.Temperature)
                .ToList();

            if (usable.Count > 0)
            {
                return usable.Average();
            }
        }

        return -1;
    }

    private static double ResolveHashrate(AgentGpuTelemetrySample sample)
    {
        if (sample.CurrentHashrate >= 0)
        {
            return sample.CurrentHashrate;
        }

        if (sample.GpuDevices.Count > 0)
        {
            var usable = sample.GpuDevices
                .Where(device => device.CurrentHashrate >= 0)
                .Select(device => (double)device.CurrentHashrate)
                .ToList();

            if (usable.Count > 0)
            {
                return usable.Sum();
            }
        }

        return -1;
    }

    private static (double Scale, string UnitLabel) ResolveHashrateScale(double maxHashrate)
    {
        if (maxHashrate >= 1_000_000_000_000)
        {
            return (1_000_000_000_000d, "TH/s");
        }

        if (maxHashrate >= 1_000_000_000)
        {
            return (1_000_000_000d, "GH/s");
        }

        if (maxHashrate >= 1_000_000)
        {
            return (1_000_000d, "MH/s");
        }

        if (maxHashrate >= 1_000)
        {
            return (1_000d, "kH/s");
        }

        return (1d, "H/s");
    }

    private byte[] BuildLoadBalancingPng(WorkUnitLifecycleExport export)
    {
        var grouped = export.Records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.AgentKey) ? "unknown" : r.AgentKey)
            .Select(g => new
            {
                AgentKey = Truncate(g.Key, 36),
                Coverage = g
                    .GroupBy(record => record.RequestId)
                        .Select(request => EstimateCoverageUnits(request.First().Mode, request.First().ChunkSummary))
                    .Sum()
            })
            .Where(x => x.Coverage > 0)
            .OrderByDescending(x => x.Coverage)
            .Take(8)
            .ToList();

        var plot = new Plot();
        plot.Title("Dynamic Load Balancing: Coverage Share");

        if (grouped.Count == 0)
        {
            plot.Add.Text("No coverage data available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1200, 700);
        }

        var values = grouped.Select(x => x.Coverage).ToArray();
        var pie = plot.Add.Pie(values);
        var total = Math.Max(1, values.Sum());

        for (var i = 0; i < grouped.Count; i++)
        {
            var share = values[i] / total * 100.0;
            pie.Slices[i].Label = $"{grouped[i].AgentKey} ({share:F1}%)";
        }

        foreach (var slice in pie.Slices)
        {
            slice.LabelStyle.IsVisible = true;
        }

        plot.HideGrid();
        return RenderPlotToPngBytes(plot, 1200, 700);
    }

    private double EstimateCoverageUnits(string? mode, string? chunkSummary)
    {
        var summary = chunkSummary?.Trim() ?? string.Empty;
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        var metric = normalized switch
        {
            "mask" => "keyspace",
            "hybrid" => "keyspace",
            "dictionary" => "bytes",
            "association" => "bytes",
            "combinator" => "bytes",
            _ => summary.Contains(";range=", StringComparison.OrdinalIgnoreCase) ? "keyspace" : "bytes"
        };

        if (metric == "keyspace")
        {
            if (TryParseInclusiveRange(ExtractAfter(summary, ";range="), out var start, out var end))
            {
                return Math.Max(0, end - start + 1);
            }

            return 1;
        }

        if (summary.StartsWith("left=", StringComparison.OrdinalIgnoreCase) && summary.Contains(";right=", StringComparison.OrdinalIgnoreCase))
        {
            var splitIndex = summary.IndexOf(";right=", StringComparison.OrdinalIgnoreCase);
            var leftSegment = splitIndex >= 0 ? summary[..splitIndex] : summary;
            var rightSegment = splitIndex >= 0 ? summary[(splitIndex + 1)..] : string.Empty;
            return Math.Max(1, ParseBracketedByteRange(leftSegment, "left=") + ParseBracketedByteRange(rightSegment, "right="));
        }

        if (TryExtractUrlByteRange(summary, out var urlStart, out var urlEnd))
        {
            return Math.Max(1, urlEnd - urlStart + 1);
        }

        if (summary.StartsWith("wordlist=", StringComparison.OrdinalIgnoreCase) && summary.Contains(";bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var wordlistName = ExtractBetween(summary, "wordlist=", ";bytes=");
            var range = ExtractAfter(summary, ";bytes=");

            if (range.Contains("EOF", StringComparison.OrdinalIgnoreCase))
            {
                var hasStart = TryParseStartOnly(range, out var startByte);
                return Math.Max(1, ResolveWordlistCoverageFromName(wordlistName, hasStart ? startByte : 0));
            }

            if (TryParseInclusiveRange(range, out var bytesStart, out var bytesEnd))
            {
                return Math.Max(1, bytesEnd - bytesStart + 1);
            }
        }

        if (TryParseInclusiveRange(ExtractAfter(summary, ";bytes="), out var fallbackBytesStart, out var fallbackBytesEnd))
        {
            return Math.Max(1, fallbackBytesEnd - fallbackBytesStart + 1);
        }

        return 1;
    }

    private static string ExtractAfter(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        return text[(start + marker.Length)..];
    }

    private static string ExtractBetween(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += startMarker.Length;
        var end = text.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
        return end >= start ? text[start..end] : text[start..];
    }

    private double ParseBracketedByteRange(string segment, string prefix)
    {
        var value = ExtractAfter(segment, prefix);
        var wordlistName = ExtractWordlistName(segment, prefix);
        var bracketStart = value.LastIndexOf('[');
        var bracketEnd = value.LastIndexOf(']');
        if (bracketStart < 0 || bracketEnd <= bracketStart)
        {
            return 1;
        }

        var range = value[(bracketStart + 1)..bracketEnd];
        var hasStart = TryParseStartOnly(range, out var start);
        if (range.Contains("EOF", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(1, ResolveWordlistCoverageFromName(wordlistName, hasStart ? start : 0));
        }

        if (!TryParseInclusiveRange(range, out var parsedStart, out var end))
        {
            if (TryParseStartOnly(range, out parsedStart))
            {
                return Math.Max(1, ResolveWordlistCoverageFromName(wordlistName, parsedStart));
            }

            return 1;
        }

        if (end < parsedStart)
        {
            return Math.Max(1, ResolveWordlistCoverageFromName(wordlistName, parsedStart));
        }

        return Math.Max(1, end - parsedStart + 1);
    }

    private bool TryExtractUrlByteRange(string summary, out long start, out long end)
    {
        start = 0;
        end = 0;
        var marker = summary.StartsWith("dictionary_url=", StringComparison.OrdinalIgnoreCase)
            ? "dictionary_url="
            : summary.StartsWith("association_url=", StringComparison.OrdinalIgnoreCase)
                ? "association_url="
                : string.Empty;
        if (string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        var urlValue = ExtractAfter(summary, marker);
        if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var wordlistName = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("startByte", out var startValue) || !long.TryParse(startValue.ToString(), out start))
        {
            start = 0;
        }

        if (!query.TryGetValue("endByte", out var endValue))
        {
            return ResolveWordlistCoverageFromName(wordlistName, start, out start, out end);
        }

        var endText = endValue.ToString();
        if (string.Equals(endText, "EOF", StringComparison.OrdinalIgnoreCase) || !long.TryParse(endText, out end))
        {
            return ResolveWordlistCoverageFromName(wordlistName, start, out start, out end);
        }

        return true;
    }

    private bool ResolveWordlistCoverageFromName(string wordlistName, long start, out long resolvedStart, out long resolvedEnd)
    {
        resolvedStart = start;
        resolvedEnd = start;
        if (string.IsNullOrWhiteSpace(wordlistName))
        {
            return true;
        }

        try
        {
            var fileLength = _wordlistService.GetWordlistFileSize(wordlistName);
            if (fileLength <= 0)
            {
                return true;
            }

            resolvedEnd = fileLength - 1;
            return true;
        }
        catch
        {
            return true;
        }
    }

    private double ResolveWordlistCoverageFromName(string wordlistName, long start)
    {
        if (string.IsNullOrWhiteSpace(wordlistName))
        {
            return 1;
        }

        try
        {
            var fileLength = _wordlistService.GetWordlistFileSize(wordlistName);
            return Math.Max(1, fileLength - start);
        }
        catch
        {
            return 1;
        }
    }

    private static string ExtractWordlistName(string segment, string prefix)
    {
        var value = ExtractAfter(segment, prefix);
        var bracketStart = value.LastIndexOf('[');
        if (bracketStart > 0)
        {
            return value[..bracketStart].Trim();
        }

        return value.Trim();
    }

    private static bool TryParseInclusiveRange(string? range, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var pieces = range.Split('-', 2, StringSplitOptions.TrimEntries);
        if (pieces.Length != 2 || !long.TryParse(pieces[0], out start))
        {
            return false;
        }

        if (string.Equals(pieces[1], "EOF", StringComparison.OrdinalIgnoreCase) || !long.TryParse(pieces[1], out end))
        {
            end = start;
        }

        return true;
    }

    private static bool TryParseStartOnly(string? range, out long start)
    {
        start = 0;
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var pieces = range.Split('-', 2, StringSplitOptions.TrimEntries);
        return pieces.Length >= 1 && long.TryParse(pieces[0], out start);
    }

    private static byte[] BuildMeanWorkTimePng(WorkUnitLifecycleExport export)
    {
        var grouped = export.Records
            .Where(r => r.ProcessingDurationSeconds >= 0)
            .GroupBy(r => string.IsNullOrWhiteSpace(r.AgentKey) ? "unknown" : r.AgentKey)
            .Select(g => new
            {
                AgentKey = Truncate(g.Key, 28),
                Mean = g.Average(r => r.ProcessingDurationSeconds),
                Count = g.Count()
            })
            .OrderByDescending(x => x.Mean)
            .Take(8)
            .ToList();

        var plot = new Plot();
        plot.Title("Mean Work Time Per Agent");
        plot.YLabel("Mean Work Unit Time (seconds)");

        if (grouped.Count == 0)
        {
            plot.Add.Text("No mean work-time data available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1200, 700);
        }

        var means = grouped.Select(x => x.Mean).ToArray();
        plot.Add.Bars(means);
        var positions = Enumerable.Range(0, grouped.Count).Select(i => (double)i).ToArray();
        var labels = grouped.Select(x => $"{x.AgentKey}\n(n={x.Count})").ToArray();
        plot.Axes.Bottom.SetTicks(positions, labels);

        return RenderPlotToPngBytes(plot, 1200, 700);
    }

    private static byte[] BuildDurationDistributionPng(WorkUnitLifecycleExport export)
    {
        var durations = export.Records
            .Select(r => r.ProcessingDurationSeconds)
            .Where(v => v >= 0)
            .OrderBy(v => v)
            .ToList();

        var plot = new Plot();
        plot.Title("WU Duration Distribution");
        plot.XLabel("Processing Duration (seconds)");
        plot.YLabel("Work Unit Count");

        if (durations.Count == 0)
        {
            plot.Add.Text("No duration distribution available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1200, 700);
        }

        const int binCount = 12;
        var max = Math.Max(0.1, durations[^1]);
        var binSize = max / binCount;
        if (binSize <= 0) binSize = 1;

        var bins = new double[binCount];
        var counts = new double[binCount];

        for (var i = 0; i < binCount; i++)
        {
            bins[i] = (i + 0.5) * binSize;
        }

        foreach (var duration in durations)
        {
            var idx = Math.Min(binCount - 1, (int)(duration / binSize));
            counts[idx]++;
        }

        plot.Add.Bars(counts);
        var tickLabels = bins.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)).ToArray();
        var tickPositions = Enumerable.Range(0, binCount).Select(i => (double)i).ToArray();
        plot.Axes.Bottom.SetTicks(tickPositions, tickLabels);

        var p95 = Percentile(durations, 0.95);
        var p95BinIndex = Math.Clamp((int)(p95 / binSize), 0, binCount - 1);
        var p95Line = plot.Add.VerticalLine(p95BinIndex);
        p95Line.Color = Colors.Red;
        p95Line.LineWidth = 2;
        p95Line.Text = $"P95 = {p95:F2}s";

        return RenderPlotToPngBytes(plot, 1200, 700);
    }

    private static byte[] RenderPlotToPngBytes(Plot plot, int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dpcs-report-{Guid.NewGuid():N}.png");
        try
        {
            plot.SavePng(path, width, height);
            return File.ReadAllBytes(path);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max] + "...";
    }

    private static double Percentile(List<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0;
        if (percentile <= 0)
            return sortedValues[0];
        if (percentile >= 1)
            return sortedValues[^1];

        var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        index = Math.Clamp(index, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }
}
