using System.Diagnostics;
using System.IO;
using System.Reflection;

internal static class LongDiagnostics
{
    private static readonly string[] Modes =
    [
        "long-combined",
        "long-scroll-only",
        "long-selection-only",
        "long-selection-then-jump",
        "long-selected-arrow",
        "long-selected-wheel"
    ];

    internal static int Run()
    {
        var path = Directory.CreateTempSubdirectory("HagoromoChan-M35-").FullName;
        Console.WriteLine($"M35 diagnostic logs: {path}");
        Console.WriteLine("Wheel is a synthetic routed preview event, not physical input; the child log identifies hovered-body vs current-history fallback.");
        var failed = false;
        foreach (var mode in Modes)
        {
            try { failed |= !RunOne(path, mode); }
            catch (Exception ex)
            {
                failed = true;
                Console.WriteLine($"{mode}: RUNNER ERROR: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return failed ? 1 : 0;
    }

    private static bool RunOne(string path, string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the test host executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // dotnet run usually uses the apphost; running dotnet <dll> also works.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the test child.");
        var outputRead = child.StandardOutput.ReadToEndAsync();
        var errorRead = child.StandardError.ReadToEndAsync();
        var timedOut = !child.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds);
        if (timedOut)
        {
            // This handle refers only to the diagnostic process started above.
            try { child.Kill(); }
            catch (InvalidOperationException) when (child.HasExited) { }
            child.WaitForExit();
        }

        var output = outputRead.GetAwaiter().GetResult();
        var error = errorRead.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(path, mode + ".out.log"), output);
        File.WriteAllText(Path.Combine(path, mode + ".err.log"), error);
        var outLines = Lines(output);
        var errorLines = Lines(error);
        var stage = outLines.LastOrDefault(line => line.StartsWith("M35 STAGE:", StringComparison.Ordinal)) ?? "no stage";
        var symptom = errorLines.FirstOrDefault(line => line.Contains("TextBoxView.", StringComparison.Ordinal))
            ?? errorLines.FirstOrDefault();
        var pass = !timedOut && child.ExitCode == 0 && outLines.Contains($"PASS M35 {mode}", StringComparer.Ordinal);
        Console.WriteLine($"{mode}: {(timedOut ? "TIMEOUT" : pass ? "PASS" : "FAIL")} (exit {child.ExitCode}); {stage}" +
            (symptom is null ? "" : $"; {symptom.Trim()}"));
        return pass;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
