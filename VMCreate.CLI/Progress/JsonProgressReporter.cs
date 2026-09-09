using System;
using System.Text.Json;

namespace VMCreate.CLI.Progress
{
    /// <summary>
    /// Emits one JSON line to stdout per progress event (NDJSON / JSON Lines).
    /// Designed for consumption by automated test harnesses and CI pipelines.
    ///
    /// Schema: {"phase":"Download","percentage":42,"speed_mbps":12.3,"uri":"https://..."}
    /// </summary>
    public class JsonProgressReporter : IProgress<CreateVMProgressInfo>
    {
        private static readonly JsonSerializerOptions _opts = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        public void Report(CreateVMProgressInfo value)
        {
            if (value == null) return;

            var payload = new ProgressPayload
            {
                Phase = value.Phase.ToString(),
                Percentage = value.ProgressPercentage > 0 ? value.ProgressPercentage : null,
                SpeedMbps = value.DownloadSpeed > 0 ? Math.Round(value.DownloadSpeed, 2) : null,
                Uri = !string.IsNullOrEmpty(value.URI) ? value.URI : null,
                // DetectedGeneration is int? (1 = MBR/BIOS, 2 = UEFI/GPT, null = undetected);
                // the NDJSON schema keeps it a string.
                DetectedGeneration = value.DetectedGeneration?.ToString(),
            };

            Console.WriteLine(JsonSerializer.Serialize(payload, _opts));
        }

        public void ReportError(string phase, string message)
        {
            var payload = new ErrorPayload { Phase = phase, Error = message };
            Console.Error.WriteLine(JsonSerializer.Serialize(payload, _opts));
        }

        private sealed class ProgressPayload
        {
            public string Phase { get; set; }
            public int? Percentage { get; set; }
            public double? SpeedMbps { get; set; }
            public string Uri { get; set; }
            public string DetectedGeneration { get; set; }
        }

        private sealed class ErrorPayload
        {
            public string Phase { get; set; }
            public string Error { get; set; }
        }
    }
}
