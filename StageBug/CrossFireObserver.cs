using System.Diagnostics;

namespace StageBug;

public sealed record CrossFireObservation(
    bool ProcessDetected,
    int? ProcessId,
    string? ExecutablePath,
    string? ProcessName,
    string? MainWindowTitle,
    bool MainWindowReady,
    IReadOnlyList<string> Modules,
    DateTimeOffset ObservedAt);

public sealed class CrossFireObserver
{
    private static readonly string[] CandidateNames =
    {
        "crossfire",
        "crossfire_x64"
    };

    public CrossFireObservation Observe()
    {
        Process? process = null;
        try
        {
            process = FindCandidateProcess();
            if (process is null)
            {
                return new CrossFireObservation(
                    false, null, null, null, null, false,
                    Array.Empty<string>(), DateTimeOffset.Now);
            }

            var modules = GetModuleNames(process);
            string? executablePath = TryGetExecutablePath(process);
            string? windowTitle = TryGetMainWindowTitle(process);
            bool windowReady = process.MainWindowHandle != IntPtr.Zero &&
                              !string.IsNullOrWhiteSpace(windowTitle);

            return new CrossFireObservation(
                true,
                process.Id,
                executablePath,
                process.ProcessName,
                windowTitle,
                windowReady,
                modules,
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            StageBugDiagnostics.Warning($"CrossFire observation failed: {ex.GetType().Name}: {ex.Message}");
            return new CrossFireObservation(
                false, null, null, null, null, false,
                Array.Empty<string>(), DateTimeOffset.Now);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static Process? FindCandidateProcess()
    {
        foreach (var candidate in CandidateNames)
        {
            var process = Process.GetProcessesByName(candidate)
                .OrderByDescending(p => SafeStartTime(p))
                .FirstOrDefault();
            if (process is not null)
                return process;
        }

        return null;
    }

    private static DateTime SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetModuleNames(Process process)
    {
        try
        {
            return process.Modules
                .Cast<ProcessModule>()
                .Select(m => m.ModuleName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
