using Spectre.Console;
using System;

namespace VMCreate.CLI.Progress
{
    /// <summary>
    /// Renders VM creation progress as a live Spectre.Console table with phase rows.
    /// Used when output is an interactive terminal (--format text).
    /// </summary>
    public class ConsoleProgressReporter : IProgress<CreateVMProgressInfo>
    {
        private readonly Table _table;
        private readonly LiveDisplayContext _ctx;
        private string _currentPhase = string.Empty;

        // Called once by the factory after starting the live display
        internal ConsoleProgressReporter(Table table, LiveDisplayContext ctx)
        {
            _table = table;
            _ctx = ctx;
        }

        public void Report(CreateVMProgressInfo value)
        {
            if (value == null) return;

            string phase = value.Phase ?? string.Empty;
            string pct = value.ProgressPercentage > 0 ? $" {value.ProgressPercentage}%" : string.Empty;
            string speed = value.DownloadSpeed > 0 ? $"  [grey]{value.DownloadSpeed:F1} MB/s[/]" : string.Empty;
            string uri = !string.IsNullOrEmpty(value.URI) && value.ProgressPercentage == 0
                ? $"  [grey]{Markup.Escape(value.URI)}[/]"
                : string.Empty;

            if (phase != _currentPhase && !string.IsNullOrEmpty(phase))
            {
                _currentPhase = phase;
                _table.AddRow(
                    $"[yellow]→[/] [bold]{Markup.Escape(phase)}[/]",
                    string.Empty);
            }

            // Update the last row with current progress
            if (_table.Rows.Count > 0)
            {
                string status = phase == "Done"
                    ? "[green]✓[/]"
                    : $"[yellow]→[/] [bold]{Markup.Escape(phase)}[/]";

                string detail = $"{pct}{speed}{uri}".Trim();

                _table.UpdateCell(_table.Rows.Count - 1, 0, status);
                _table.UpdateCell(_table.Rows.Count - 1, 1, detail);
            }

            _ctx.Refresh();
        }

        public void ReportDone()
        {
            // Mark last row green
            if (_table.Rows.Count > 0)
            {
                _table.UpdateCell(_table.Rows.Count - 1, 0, "[green]✓[/] [bold]Done[/]");
                _table.UpdateCell(_table.Rows.Count - 1, 1, string.Empty);
                _ctx.Refresh();
            }
        }

        public void ReportError(string phase, string message)
        {
            if (_table.Rows.Count > 0)
            {
                _table.UpdateCell(_table.Rows.Count - 1, 0, $"[red]✗[/] [bold]{Markup.Escape(phase)}[/]");
                _table.UpdateCell(_table.Rows.Count - 1, 1, $"[red]{Markup.Escape(message)}[/]");
                _ctx.Refresh();
            }
        }
    }
}
