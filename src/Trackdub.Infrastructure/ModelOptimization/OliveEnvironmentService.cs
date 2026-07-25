using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Trackdub.Contracts;
using Trackdub.Contracts.ModelOptimization;

namespace Trackdub.Infrastructure.ModelOptimization;

public sealed class OliveEnvironmentService : IOliveEnvironmentService
{
    private static readonly Version MinPythonVersion = new(3, 10);

    private readonly StreamingProcessRunner _runner = new();
    private readonly string _venvRoot;

    public OliveEnvironmentService(IAppStoragePaths storagePaths)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        _venvRoot = storagePaths.ToolCacheDirectory;
    }

    public string GetManagedPythonPath(OliveExecutionProvider provider)
    {
        string venvPath = GetVenvPath(provider);
        return OperatingSystem.IsWindows()
            ? Path.Combine(venvPath, "Scripts", "python.exe")
            : Path.Combine(venvPath, "bin", "python");
    }

    private string GetManagedPipPath(OliveExecutionProvider provider)
    {
        string venvPath = GetVenvPath(provider);
        return OperatingSystem.IsWindows()
            ? Path.Combine(venvPath, "Scripts", "pip.exe")
            : Path.Combine(venvPath, "bin", "pip");
    }

    private string GetVenvPath(OliveExecutionProvider provider) =>
        Path.Combine(_venvRoot, $"olive-env-{provider.ToString().ToLowerInvariant()}");

    public string GetOliveExecutablePath(OliveExecutionProvider provider)
    {
        string? systemOlive = FindSystemOlive();
        if (systemOlive is not null)
            return systemOlive;

        string scriptsDir = Path.GetDirectoryName(GetManagedPythonPath(provider))!;
        return OperatingSystem.IsWindows()
            ? Path.Combine(scriptsDir, "olive.exe")
            : Path.Combine(scriptsDir, "olive");
    }

    public async Task<OliveEnvironmentStatus> GetStatusAsync(
        OliveExecutionProvider provider,
        CancellationToken cancellationToken)
    {
        string? systemOlive = FindSystemOlive();
        if (systemOlive is not null)
        {
            return new OliveEnvironmentStatus(
                PythonAvailable: true,
                PythonVersion: null,
                VenvExists: false,
                OliveInstalled: true,
                SystemOlivePath: systemOlive);
        }

        string? pythonVersion = await TryGetPythonVersionAsync(cancellationToken).ConfigureAwait(false);
        string pythonPath = GetManagedPythonPath(provider);
        bool venvExists = Directory.Exists(GetVenvPath(provider)) && File.Exists(pythonPath)
            && await IsManagedPythonHealthyAsync(provider, cancellationToken).ConfigureAwait(false);
        bool oliveInstalled = venvExists && await IsOliveInstalledAsync(provider, cancellationToken).ConfigureAwait(false);

        return new OliveEnvironmentStatus(
            PythonAvailable: pythonVersion is not null,
            PythonVersion: pythonVersion,
            VenvExists: venvExists,
            OliveInstalled: oliveInstalled);
    }

    public async IAsyncEnumerable<string> BootstrapAsync(
        OliveExecutionProvider provider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? systemOlive = FindSystemOlive();
        if (systemOlive is not null)
        {
            string? version = await TryGetOliveVersionAsync(systemOlive, cancellationToken).ConfigureAwait(false);
            yield return version is not null
                ? $"Using system Olive on PATH: {systemOlive} ({version})"
                : $"Using system Olive on PATH: {systemOlive}";

            yield return $"Olive environment ({provider}) ready.";
            yield break;
        }

        string venvPath = GetVenvPath(provider);
        string pythonPath = GetManagedPythonPath(provider);

        var (systemPython, installLines) = await EnsurePythonAsync(cancellationToken).ConfigureAwait(false);
        foreach (string line in installLines)
        {
            yield return line;
        }

        // Heal a stale venv whose interpreter no longer runs (its base Python was moved or
        // uninstalled — the Scripts\python.exe stub then points at a missing executable).
        // Recreate it instead of running pip against the dead stub.
        if (Directory.Exists(venvPath) && File.Exists(pythonPath)
            && !await IsManagedPythonHealthyAsync(provider, cancellationToken).ConfigureAwait(false))
        {
            yield return $"Existing Olive venv interpreter is not runnable (base Python missing); recreating {venvPath}…";
            TryDeleteDirectory(venvPath);
        }

        if (!Directory.Exists(venvPath) || !File.Exists(pythonPath))
        {
            yield return $"Creating venv at {venvPath}…";
            await foreach (string line in _runner.RunAsync(
                systemPython, ["-m", "venv", venvPath], Environment.CurrentDirectory, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return line;
            }
        }

        await foreach (string line in UpgradeManagedPipAsync(provider, cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }

        foreach (string pkg in GetRequiredPackages(provider))
        {
            yield return $"Installing {pkg}…";
            await foreach (string line in _runner.RunAsync(
                GetManagedPipPath(provider),
                ["install", pkg, "--quiet", "--upgrade"],
                Environment.CurrentDirectory,
                cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }

        yield return $"Olive environment ({provider}) ready.";
    }

    private async IAsyncEnumerable<string> UpgradeManagedPipAsync(
        OliveExecutionProvider provider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return "Upgrading pip…";
        await foreach (string line in _runner.RunAsync(
            GetManagedPythonPath(provider),
            ["-m", "pip", "install", "--upgrade", "pip", "--quiet"],
            Environment.CurrentDirectory,
            cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    private const string OrtPackageSpec = "onnxruntime>=1.22.0,<2.0.0";
    private const string OrtGpuPackageSpec = "onnxruntime-gpu>=1.22.0,<2.0.0";
    private const string OrtDirectMlPackageSpec = "onnxruntime-directml>=1.22.0,<2.0.0";
    private const string RequestsPackageSpec = "requests>=2.32.0,<3.0.0";

    private static string OlivePackage(params string[] extras)
    {
        string extrasText = extras.Length == 0
            ? string.Empty
            : $"[{string.Join(",", extras)}]";
        return $"olive-ai{extrasText}>=0.9.0,<1.0.0";
    }

    private static IReadOnlyList<string> GetRequiredPackages(OliveExecutionProvider provider) =>
        provider switch
        {
            OliveExecutionProvider.Dml => [OlivePackage(), OrtDirectMlPackageSpec, RequestsPackageSpec],
            OliveExecutionProvider.Cuda or OliveExecutionProvider.TensorRt => [OlivePackage(), OrtGpuPackageSpec, RequestsPackageSpec],
            OliveExecutionProvider.TensorRtRtx => [OlivePackage(), OrtPackageSpec, RequestsPackageSpec],
            OliveExecutionProvider.Qnn => [OlivePackage("qnn"), OrtPackageSpec, RequestsPackageSpec],
            OliveExecutionProvider.OpenVino => [OlivePackage("openvino"), OrtPackageSpec, RequestsPackageSpec],
            OliveExecutionProvider.Migraphx or OliveExecutionProvider.Rocm => [OlivePackage(), OrtPackageSpec, RequestsPackageSpec],
            OliveExecutionProvider.VitisAi => [OlivePackage(), OrtPackageSpec, RequestsPackageSpec],
            _ => [OlivePackage(), RequestsPackageSpec],
        };

    // Test hook — allows Composition.Tests / Infrastructure.Tests to verify pinned specs without
    // instantiating the full service (avoids I/O on test startup).
    internal static IReadOnlyList<string> GetRequiredPackagesForTest(OliveExecutionProvider provider) =>
        GetRequiredPackages(provider);

    private async Task<(string Python, IReadOnlyList<string> LogLines)> EnsurePythonAsync(
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        string? version = await TryGetPythonVersionAsync(cancellationToken).ConfigureAwait(false);
        if (version is not null)
        {
            return (OperatingSystem.IsWindows() ? "python" : "python3", lines);
        }

        if (OperatingSystem.IsWindows())
        {
            lines.Add("Python not found — installing via winget…");
            await RunFireAndForgetAsync(
                "winget",
                ["install", "Python.Python.3.11", "--silent",
                 "--accept-package-agreements", "--accept-source-agreements"],
                cancellationToken).ConfigureAwait(false);
            version = await TryGetPythonVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                throw new InvalidOperationException(
                    "Python 3.10+ could not be installed automatically. " +
                    "Please install it from https://python.org and re-run.");
            }
            return ("python", lines);
        }

        if (OperatingSystem.IsMacOS())
        {
            lines.Add("Python not found — attempting brew install python@3.11…");
            await RunFireAndForgetAsync("brew", ["install", "python@3.11"], cancellationToken).ConfigureAwait(false);
            version = await TryGetPythonVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                throw new InvalidOperationException(
                    "Python 3.10+ could not be installed. " +
                    "Install Homebrew (https://brew.sh) or Python directly from https://python.org.");
            }
            return ("python3", lines);
        }

        throw new InvalidOperationException(
            "Python 3.10+ is required but was not found. " +
            "Install it with your package manager: apt install python3.11 / dnf install python3.11");
    }

    private async Task<string?> TryGetPythonVersionAsync(CancellationToken cancellationToken)
    {
        foreach (string candidate in PythonCandidates())
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo(candidate)
                    {
                        ArgumentList = { "--version" },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                string versionText = string.IsNullOrWhiteSpace(output) ? error : output;
                Match match = Regex.Match(versionText, @"Python (\d+\.\d+)");
                if (match.Success && Version.TryParse(match.Groups[1].Value, out Version? v) && v >= MinPythonVersion)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
            }
        }

        return null;
    }

    private async Task<bool> IsOliveInstalledAsync(OliveExecutionProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(GetManagedPipPath(provider))
                {
                    ArgumentList = { "show", "olive-ai" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunFireAndForgetAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
        }
    }

    private static string? FindSystemOlive()
    {
        string executable = OperatingSystem.IsWindows() ? "olive.exe" : "olive";
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static async Task<string?> TryGetOliveVersionAsync(string olivePath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(olivePath)
                {
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string version = output.Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }
    }

    // Verifies the managed venv interpreter actually runs. A venv created from a Python that was
    // later moved/uninstalled leaves a stub python.exe that throws "cannot find the file specified"
    // when launched; treat that as not-healthy so the venv is recreated and status stays honest.
    private async Task<bool> IsManagedPythonHealthyAsync(OliveExecutionProvider provider, CancellationToken cancellationToken)
    {
        string pythonPath = GetManagedPythonPath(provider);
        if (!File.Exists(pythonPath))
            return false;

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(pythonPath)
                {
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string versionText = string.IsNullOrWhiteSpace(output) ? error : output;
            return process.ExitCode == 0 && Regex.IsMatch(versionText, @"Python \d+\.\d+");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort; the recreation step will surface any remaining issue.
        }
    }

    private static IEnumerable<string> PythonCandidates() =>
        OperatingSystem.IsWindows() ? ["python", "python3"] : ["python3", "python"];
}
