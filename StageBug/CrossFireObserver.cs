using System.Diagnostics;

namespace StageBug;

public sealed record CrossFireObservation(
    bool ProcessDetected,
    int? ProcessId,
    string? ExecutablePath,
    string? ProcessName,
    string? MainWindowTitle,
    bool MainWindowReady,
    bool ModuleInspectionSucceeded,
    IReadOnlyList<string> Modules,
    DateTimeOffset ObservedAt)
{
    public bool ClientIdentified =>
        ProcessDetected &&
        !string.IsNullOrWhiteSpace(ExecutablePath) &&
        !string.IsNullOrWhiteSpace(ProcessName);
}

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
                return EmptyObservation();
            }

            string? executablePath = TryGetExecutablePath(process);
            string? processName = TryGetProcessName(process);
            string? windowTitle = TryGetMainWindowTitle(process);
            bool windowReady = process.MainWindowHandle != IntPtr.Zero &&
                               !string.IsNullOrWhiteSpace(windowTitle);

            var moduleResult = GetModuleNames(process);

            var observation = new CrossFireObservation(
                true,
                process.Id,
                executablePath,
                processName,
                windowTitle,
                windowReady,
                moduleResult.Succeeded,
                moduleResult.Modules,
                DateTimeOffset.Now);

            StageBugDiagnostics.Info(
                $"CrossFire observation: pid={observation.ProcessId}, " +
                $"identified={observation.ClientIdentified}, " +
                $"windowReady={observation.MainWindowReady}, " +
                $"moduleInspection={observation.ModuleInspectionSucceeded}, " +
                $"modules={observation.Modules.Count}");

            return observation;
        }
        catch (Exception ex)
        {
            StageBugDiagnostics.Warning($"CrossFire observation failed: {ex.GetType().Name}: {ex.Message}");
            return EmptyObservation();
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static CrossFireObservation EmptyObservation() => new(
        false, null, null, null, null, false, false,
        Array.Empty<string>(), DateTimeOffset.Now);

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
        catch (Exception ex)
        {
            StageBugDiagnostics.Warning($"CrossFire executable path unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
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

    private static (bool Succeeded, IReadOnlyList<string> Modules) GetModuleNames(Process process)
    {
        try
        {
            var modules = process.Modules
                .Cast<ProcessModule>()
                .Select(m => m.ModuleName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return (true, modules);
        }
        catch (Exception ex)
        {
            StageBugDiagnostics.Warning($"CrossFire module inspection unavailable: {ex.GetType().Name}: {ex.Message}");
            return (false, Array.Empty<string>());
        }
    }
}
