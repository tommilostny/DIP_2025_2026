using System.Globalization;
using System.Text;
using ScottPlot;

namespace DPCS.Blazor.Services;

public sealed class JobReportExportService
{
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

    public byte[] BuildReportChartPng(WorkUnitLifecycleExport export, AgentGpuTelemetryExport? telemetryExport, string? chartType)
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
            _ => BuildLoadBalancingPng(export)
        };
    }

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
                Utilization = ResolveGpuUtilization(sample)
            })
            .Where(item => item.Utilization >= 0)
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

    private static byte[] BuildLoadBalancingPng(WorkUnitLifecycleExport export)
    {
        var grouped = export.Records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.AgentKey) ? "unknown" : r.AgentKey)
            .Select(g => new
            {
                AgentKey = Truncate(g.Key, 36),
                Total = g.Count()
            })
            .OrderByDescending(x => x.Total)
            .Take(8)
            .ToList();

        var plot = new Plot();
        plot.Title("Dynamic Load Balancing: Work Unit Share");

        if (grouped.Count == 0)
        {
            plot.Add.Text("No work-unit data available yet", 0.5, 0.5);
            plot.HideGrid();
            plot.Axes.Frameless();
            return RenderPlotToPngBytes(plot, 1200, 700);
        }

        var values = grouped.Select(x => (double)x.Total).ToArray();
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

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
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
