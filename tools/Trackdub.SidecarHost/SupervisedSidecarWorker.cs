using System.Diagnostics;
using System.Text.Json;

namespace Trackdub.SidecarHost;

/// <summary>
/// C# host-side mirror of workers/supervisor/src/supervisor.rs: spawns the worker
/// with piped stdio, speaks JSON-lines with strictly ordered id-matched requests,
/// enforces the protocol version stamp, poisons the connection on timeout or
/// id-mismatch, and kills the child on dispose. Requests are one-in-flight.
/// </summary>
public sealed class SupervisedSidecarWorker : IAsyncDisposable
{
    public const int ExpectedProtocolVersion = 1;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// One-in-flight request timeout. Mirrors the supervisor's gate window for
    /// short ops (health/infer), but cold model loads legitimately take several
    /// minutes, so <see cref="Request"/> accepts an explicit longer timeout.
    /// </summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(10);

    private readonly Process _process;
    private readonly StreamReader _stdout;
    private readonly StreamWriter _stdin;
    private bool _poisoned;

    private SupervisedSidecarWorker(Process process, StreamReader stdout, StreamWriter stdin)
    {
        _process = process;
        _stdout = stdout;
        _stdin = stdin;
    }

    public int ProcessId => _process.Id;

    public static SupervisedSidecarWorker Spawn(
        string program,
        string[] args,
        string? workingDirectory = null,
        Dictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,   // worker stderr inherits, same as supervisor.rs
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        Process process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to spawn {program}");
        return new SupervisedSidecarWorker(process, process.StandardOutput, process.StandardInput);
    }

    public SidecarResponse Request(SidecarRequest request, TimeSpan? timeout = null)
    {
        if (_poisoned)
        {
            throw new InvalidOperationException("worker connection is unusable after a timeout or out-of-order response; respawn first");
        }

        TimeSpan responseTimeout = timeout ?? ResponseTimeout;

        _stdin.WriteLine(JsonSerializer.Serialize(request, ProtocolJson.Options));
        _stdin.Flush();

        Task<string?> read = _stdout.ReadLineAsync();
        if (!read.Wait(responseTimeout))
        {
            Poison();
            throw new InvalidOperationException($"worker response timed out after {responseTimeout.TotalSeconds}s");
        }

        string? line = read.Result;
        if (line is null)
        {
            Poison();
            throw new InvalidOperationException("worker stdout closed");
        }

        var response = JsonSerializer.Deserialize<SidecarResponse>(line, ProtocolJson.Options)
            ?? throw new InvalidOperationException($"unparseable worker response: {line}");
        if (response.ProtocolVersion != ExpectedProtocolVersion)
        {
            Poison();
            throw new InvalidOperationException($"worker protocol version {response.ProtocolVersion} != {ExpectedProtocolVersion}; refusing to serve");
        }
        if (response.Id != request.Id)
        {
            Poison();
            throw new InvalidOperationException($"worker response id {response.Id} != request id {request.Id}; stream out of order");
        }

        return response;
    }

    private void Poison()
    {
        _poisoned = true;
    }

    public async ValueTask DisposeAsync()
    {
        // kill-on-drop, mirroring SupervisedWorker::Drop in supervisor.rs
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort: never throw from dispose
        }

        _process.Dispose();
        await Task.CompletedTask;
    }
}
