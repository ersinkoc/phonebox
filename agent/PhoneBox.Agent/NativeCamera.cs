using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhoneBox.Agent;

public sealed record NativeCameraStatus(bool Supported, bool Packaged, bool Installed, bool Running, bool Busy, string? Error);

/// <summary>The Windows 11 camera is opt-in and session-lifetime. Enabling it never requests phone capture.</summary>
public sealed class NativeCamera : IDisposable
{
    private const string ClassKey = @"SOFTWARE\Classes\CLSID\{4F412BC4-5E9D-463C-A8CA-6C137AF67017}\InprocServer32";
    private readonly SemaphoreSlim actions = new(1);
    private readonly object gate = new();
    private nint library, host;
    private string? error;
    private bool disposed;
    private StopCamera? stop;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int StartCamera(uint port, out nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void StopCamera(nint handle);

    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static string PackageDirectory => Path.Combine(AppContext.BaseDirectory, "camera");
    private static string InstalledDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PhoneBox", "Camera");
    private static bool Packaged => File.Exists(Path.Combine(PackageDirectory, "PhoneBoxCameraSetup.exe")) && File.Exists(Path.Combine(PackageDirectory, "PhoneBoxCamera.dll"));

    internal static bool IsExpectedPath(string? path, string directory)
    {
        try { return !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) &&
            Path.GetFullPath(path).Equals(Path.Combine(Path.GetFullPath(directory), "PhoneBoxCamera.dll"), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? InstalledPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(ClassKey);
            var path = key?.GetValue(null) as string;
            if (!IsExpectedPath(path, InstalledDirectory) || !File.Exists(path)) return null;
            foreach (var entry in new[] { InstalledDirectory, Path.GetDirectoryName(InstalledDirectory)!, path! })
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return null;
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    public NativeCameraStatus Status()
    {
        lock (gate) return new(Supported, Packaged, InstalledPath() != null, host != 0, actions.CurrentCount == 0, error);
    }

    private async Task<string?> Run(Func<Task<string?>> action)
    {
        if (!await actions.WaitAsync(0)) return "Another camera operation is in progress.";
        try
        {
            lock (gate) { if (disposed) return "The agent is stopping."; }
            var failure = await action();
            lock (gate) error = failure;
            return failure;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { lock (gate) return error = "Installation was cancelled."; }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { lock (gate) return error = "Cannot load or install PhoneBox Camera. Close camera applications and check the installed component."; }
        finally { actions.Release(); }
    }

    public Task<string?> Enable(int port) => Run(async () =>
    {
        if (!Supported) return "PhoneBox Camera requires Windows 11 x64 (build 22000 or later).";
        if (port is < 1024 or > 65535) return "Invalid agent port.";
        lock (gate) { if (host != 0) return null; }
        var path = InstalledPath();
        if (path is null) return "Install the PhoneBox Camera component first.";
        return await Task.Run(() =>
        {
            var loaded = NativeLibrary.Load(path, typeof(NativeCamera).Assembly, DllImportSearchPath.System32 | DllImportSearchPath.UseDllDirectoryForDependencies);
            try
            {
                var start = Marshal.GetDelegateForFunctionPointer<StartCamera>(NativeLibrary.GetExport(loaded, "PhoneBoxCameraStart"));
                var end = Marshal.GetDelegateForFunctionPointer<StopCamera>(NativeLibrary.GetExport(loaded, "PhoneBoxCameraStop"));
                var hr = start((uint)port, out var handle);
                if (hr < 0) return $"Windows could not enable PhoneBox Camera (0x{unchecked((uint)hr):X8}). Check camera access for desktop apps in Windows Settings.";
                lock (gate) { library = loaded; host = handle; stop = end; loaded = 0; }
                return null;
            }
            finally { if (loaded != 0) NativeLibrary.Free(loaded); }
        });
    });

    private void StopCore()
    {
        lock (gate)
        {
            if (host != 0) { stop!(host); host = 0; }
            if (library != 0) { NativeLibrary.Free(library); library = 0; }
            stop = null;
        }
    }
    public Task<string?> Disable() => Run(async () => { await Task.Run(StopCore); return null; });

    public Task<string?> Install(bool remove) => Run(async () =>
    {
        if (!Supported) return "PhoneBox Camera requires Windows 11 x64 (build 22000 or later).";
        lock (gate) { if (host != 0) return "Disable PhoneBox Camera before updating or removing its component."; }
        var helper = Path.Combine(remove ? InstalledDirectory : PackageDirectory, "PhoneBoxCameraSetup.exe");
        if (!File.Exists(helper)) return "The native camera component is missing from this agent package.";
        using var process = Process.Start(new ProcessStartInfo(helper)
        {
            Arguments = remove ? "--uninstall" : "--install", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        });
        if (process is null) return "Could not start camera setup.";
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? null : $"Camera setup failed (0x{unchecked((uint)process.ExitCode):X8}). Close camera applications and retry.";
    });

    public void Dispose()
    {
        actions.Wait();
        try { lock (gate) disposed = true; StopCore(); }
        finally { actions.Release(); }
    }
}
