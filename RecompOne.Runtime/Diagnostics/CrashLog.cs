using System.Text;

namespace RecompOne.Runtime.Diagnostics;

// Writes a crash report to a timestamped file when the process dies from an
// unhandled exception, so intermittent crashes aren't lost to a closed console.
// The report includes the exception (type/message/stack) plus the recent
// console output captured by ConsoleMirror — the [prim]/CD/FMV/etc. lines
// leading up to the crash, which are usually what pinpoint it.
//
// Caveat: a StackOverflowException cannot be caught by the CLR (the process is
// torn down immediately), so the deep-recursion tail-call path won't produce a
// report. Those show as a silent exit; everything else is captured.
public static class CrashLog
{
    static string _dir = ".";
    static bool _installed;

    public static void Install(string? dir = null)
    {
        if (_installed) return;
        _installed = true;
        if (!string.IsNullOrEmpty(dir)) _dir = dir!;
        ConsoleMirror.Install(); // ensure recent console output is available to dump
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            string path = System.IO.Path.Combine(_dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"=== {kind} @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();
            sb.AppendLine("=== recent console output (oldest first) ===");
            var lines = new List<string>();
            ConsoleMirror.SnapshotInto(lines);
            foreach (var l in lines) sb.AppendLine(l);
            System.IO.File.WriteAllText(path, sb.ToString());
            Console.Error.WriteLine($"[crash] report written to {path}");
        }
        catch { /* never let the crash handler throw */ }
    }
}
