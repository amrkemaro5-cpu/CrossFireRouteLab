using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Native
{
    [Flags]
    internal enum ProcessAccess : uint
    {
        QueryInformation = 0x0400,
        VmOperation = 0x0008,
        VmRead = 0x0010,
        VmWrite = 0x0020
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(ProcessAccess access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualProtectEx(nint process, nint address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}

internal static class Program
{
    private const nuint GateRva = 0x8FDF;

    private static readonly byte[] Expected =
    {
        0xE8, 0xAC, 0xAA, 0x00, 0x00, 0x84, 0xC0,
        0x0F, 0x84, 0x95, 0x3C, 0x00, 0x00
    };

    // Keep the original success continuation and remove only the call/result branch.
    private static readonly byte[] Patched =
    {
        0xC6, 0x86, 0x88, 0x00, 0x00, 0x00, 0x01,
        0x90, 0x90, 0x90, 0x90, 0x90, 0x90
    };

    [STAThread]
    private static int Main()
    {
        string original = Path.Combine(AppContext.BaseDirectory, "StageBug.exe");
        if (!File.Exists(original))
        {
            MessageBox.Show(
                "Place this launcher beside your original StageBug.exe.",
                "StageBug No Activation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        DateTime launchUtc = DateTime.UtcNow;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = original,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "StageBug", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }

        for (int attempt = 0; attempt < 300; attempt++)
        {
            Thread.Sleep(100);

            foreach (Process p in Process.GetProcessesByName("EpicWebHelper"))
            {
                try
                {
                    if (p.StartTime.ToUniversalTime() < launchUtc.AddSeconds(-3))
                        continue;
                    if (p.MainModule is null)
                        continue;

                    string module = p.MainModule.FileName;
                    if (!module.EndsWith("EpicWebHelper.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryPatch(p))
                        return 0;
                }
                catch
                {
                    // The runtime may be transitioning while its image is being unpacked/loaded.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        MessageBox.Show(
            "The original StageBug runtime was found, but its expected activation-gate bytes were not available.\n\nNo file on disk was modified.",
            "StageBug No Activation",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return 4;
    }

    private static bool TryPatch(Process process)
    {
        nint handle = Native.OpenProcess(
            Native.ProcessAccess.QueryInformation |
            Native.ProcessAccess.VmOperation |
            Native.ProcessAccess.VmRead |
            Native.ProcessAccess.VmWrite,
            false,
            process.Id);

        if (handle == 0)
            return false;

        try
        {
            nuint baseAddress = (nuint)process.MainModule!.BaseAddress;
            nint address = (nint)(baseAddress + GateRva);

            byte[] current = new byte[Expected.Length];
            if (!Native.ReadProcessMemory(handle, address, current, (nuint)current.Length, out nuint read) ||
                read != (nuint)current.Length)
                return false;

            if (current.SequenceEqual(Patched))
                return true;

            // Safety guard: never patch an unexpected runtime build.
            if (!current.SequenceEqual(Expected))
                return false;

            const uint PAGE_EXECUTE_READWRITE = 0x40;
            if (!Native.VirtualProtectEx(handle, address, (nuint)Patched.Length, PAGE_EXECUTE_READWRITE, out uint oldProtect))
                return false;

            try
            {
                if (!Native.WriteProcessMemory(handle, address, Patched, (nuint)Patched.Length, out nuint written) ||
                    written != (nuint)Patched.Length)
                    return false;

                if (!Native.FlushInstructionCache(handle, address, (nuint)Patched.Length))
                    return false;
            }
            finally
            {
                _ = Native.VirtualProtectEx(handle, address, (nuint)Patched.Length, oldProtect, out _);
            }

            byte[] verify = new byte[Patched.Length];
            return Native.ReadProcessMemory(handle, address, verify, (nuint)verify.Length, out nuint verifyRead) &&
                   verifyRead == (nuint)verify.Length &&
                   verify.SequenceEqual(Patched);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
