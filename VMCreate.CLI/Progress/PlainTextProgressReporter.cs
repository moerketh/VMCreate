using System;

namespace VMCreate.CLI.Progress
{
    /// <summary>
    /// Prints VM creation progress as plain text lines. Used when stdout is
    /// redirected (pipe/file), where Spectre.Console's Live display cannot
    /// render (its cursor handling throws on a redirected console).
    /// </summary>
    public class PlainTextProgressReporter : IProgress<CreateVMProgressInfo>
    {
        private string _currentPhase = string.Empty;

        public void Report(CreateVMProgressInfo value)
        {
            if (value == null) return;

            string phase = value.Phase.ToString();
            if (phase != _currentPhase && !string.IsNullOrEmpty(phase))
            {
                _currentPhase = phase;
                Console.WriteLine($"[{phase}]");
            }
            if (value.ProgressPercentage > 0)
                Console.WriteLine($"  {value.ProgressPercentage}%");
            else if (!string.IsNullOrEmpty(value.URI))
                Console.WriteLine($"  {value.URI}");
        }

        public void ReportDone() => Console.WriteLine("[Done]");

        public void ReportError(string phase, string message) =>
            Console.WriteLine($"[{phase}] ERROR: {message}");
    }
}
