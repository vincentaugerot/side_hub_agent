using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SideHub.Agent;

public class NodePtyExecutor : IAsyncDisposable
{
    private readonly string _workingDirectory;
    private readonly string _helperPath;
    private Process? _nodeProcess;
    private Func<string, Task>? _onOutput;
    private Func<int, Task>? _onExit;
    private Func<PtyForeground, Task>? _onForeground;
    private readonly object _lock = new();
    private bool _hasExited;
    private bool _isStopping;
    private Task? _readTask;
    private int _columns;
    private int _rows;
    private readonly PtyOutputBuffer _outputBuffer = new();
    private SecretMasker? _secretMasker;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingPings = new();

    private const int MaxStderrLineLength = 8192;
    private const int MaxStderrLinesLogged = 1000;

    /// <summary>How long output held back by the <see cref="SecretMasker"/> (possible start of a secret)
    /// waits for the next chunk before it is released anyway, so a quiet terminal is not left stale.</summary>
    private static readonly TimeSpan HeldOutputDelay = TimeSpan.FromMilliseconds(250);

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _nodeProcess != null && !_hasExited && !_nodeProcess.HasExited;
            }
        }
    }

    /// <summary>What runs in the foreground of the terminal, as the helper last said; null until it says (Windows).</summary>
    public PtyForeground? Foreground { get; private set; }

    /// <summary>
    /// Gets all buffered PTY output history.
    /// </summary>
    public string GetBufferedOutput() => _outputBuffer.GetAll();

    /// <summary>
    /// Gets the current buffer size in bytes.
    /// </summary>
    public int BufferSize => _outputBuffer.Size;

    /// <summary>
    /// Checks if the Node helper is responding (health check).
    /// </summary>
    public async Task<bool> IsHealthyAsync(TimeSpan? timeout = null)
    {
        if (_nodeProcess == null || _hasExited)
            return false;

        timeout ??= TimeSpan.FromSeconds(2);
        var pingId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>();
        _pendingPings[pingId] = tcs;

        try
        {
            var cmd = new { type = "ping", id = pingId };
            await SendCommandAsync(cmd, CancellationToken.None);

            using var cts = new CancellationTokenSource(timeout.Value);
            cts.Token.Register(() => tcs.TrySetResult(false));

            return await tcs.Task;
        }
        catch
        {
            return false;
        }
        finally
        {
            _pendingPings.TryRemove(pingId, out _);
        }
    }

    public NodePtyExecutor(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        _helperPath = ResolveHelperPath();
    }

    /// <summary>pty-helper/index.js next to the executable, or one folder up (development).</summary>
    public static string ResolveHelperPath()
    {
        var exeDir = AppContext.BaseDirectory;
        var helperPath = Path.Combine(exeDir, "pty-helper", "index.js");
        if (File.Exists(helperPath))
            return helperPath;

        return Path.Combine(
            Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar)) ?? "",
            "pty-helper", "index.js"
        );
    }

    public async Task StartAsync(
        string shell,
        Func<string, Task> onOutput,
        Func<int, Task> onExit,
        int columns = 80,
        int rows = 24,
        IReadOnlyDictionary<string, string>? environment = null,
        SecretMasker? secretMasker = null,
        CancellationToken ct = default,
        Func<PtyForeground, Task>? onForeground = null)
    {
        lock (_lock)
        {
            if (_nodeProcess != null)
                throw new InvalidOperationException("PTY session already running");
            _hasExited = false;
            _isStopping = false;
        }

        _onOutput = onOutput;
        _onExit = onExit;
        _onForeground = onForeground;
        _secretMasker = secretMasker;
        _columns = columns;
        _rows = rows;

        if (!File.Exists(_helperPath))
        {
            throw new FileNotFoundException($"PTY helper not found at: {_helperPath}");
        }

        // Never a bare "node": it would be looked up in the working directory (the repository) first.
        var nodePath = ExecutableResolver.Resolve("node")
            ?? throw new FileNotFoundException("node not found in PATH");

        Console.WriteLine($"[NodePty] Starting helper: {_helperPath} with {nodePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            Arguments = _helperPath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The helper speaks UTF-8 JSON lines both ways. Left unset, .NET uses the console code page
            // on Windows (437, 850…) and turns every non-ASCII character of the PTY output (box drawing,
            // spinners, accents) into "Γöé", and garbles accents typed into the terminal.
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            CreateNoWindow = true
        };

        _nodeProcess = new Process { StartInfo = startInfo };
        _nodeProcess.Start();

        // Start reading output
        _readTask = ReadOutputAsync(ct);
        // stderr is redirected: left unread, its pipe fills and the helper blocks on its next write to it.
        _ = DrainStderrAsync(_nodeProcess.StandardError);

        // Wait for ready signal
        await Task.Delay(100, ct);

        // Send start command
        var startCmd = new
        {
            type = "start",
            shell = shell,
            cwd = _workingDirectory,
            cols = columns,
            rows = rows,
            env = environment
        };

        await SendCommandAsync(startCmd, ct);
    }

    public async Task WriteAsync(string input, CancellationToken ct = default)
    {
        if (_nodeProcess == null || _hasExited)
            return;

        var cmd = new { type = "input", data = input };
        await SendCommandAsync(cmd, ct);
    }

    public async Task ResizeAsync(int columns, int rows)
    {
        if (_nodeProcess == null || _hasExited)
            return;

        _columns = columns;
        _rows = rows;

        var cmd = new { type = "resize", cols = columns, rows = rows };
        await SendCommandAsync(cmd, CancellationToken.None);
    }

    public void Resize(int columns, int rows)
    {
        _ = ResizeAsync(columns, rows);
    }

    private async Task SendCommandAsync(object cmd, CancellationToken ct)
    {
        if (_nodeProcess?.StandardInput == null) return;

        var json = JsonSerializer.Serialize(cmd);
        await _nodeProcess.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await _nodeProcess.StandardInput.FlushAsync();
    }

    private async Task ReadOutputAsync(CancellationToken ct)
    {
        if (_nodeProcess?.StandardOutput == null) return;

        try
        {
            while (!ct.IsCancellationRequested && !_hasExited)
            {
                var lineTask = _nodeProcess.StandardOutput.ReadLineAsync(ct).AsTask();
                if (_secretMasker is { HasPending: true }
                    && await Task.WhenAny(lineTask, Task.Delay(HeldOutputDelay, ct)) != lineTask)
                {
                    await EmitOutputAsync(_secretMasker.Flush());
                }
                var line = await lineTask;
                if (line == null) break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();

                    switch (type)
                    {
                        case "ready":
                            Console.WriteLine("[NodePty] Helper ready");
                            break;

                        case "started":
                            var shell = root.GetProperty("shell").GetString();
                            Console.WriteLine($"[NodePty] PTY started with {shell}");
                            break;

                        case "output":
                            var data = root.GetProperty("data").GetString();
                            if (data != null)
                            {
                                await EmitOutputAsync(_secretMasker?.Process(data) ?? data);
                            }
                            break;

                        case "exit":
                            var exitCode = root.GetProperty("exitCode").GetInt32();
                            Console.WriteLine($"[NodePty] PTY exited with code {exitCode}");
                            lock (_lock)
                            {
                                _hasExited = true;
                                if (_isStopping) return;
                            }
                            if (_secretMasker != null)
                            {
                                await EmitOutputAsync(_secretMasker.Flush());
                            }
                            if (_onExit != null)
                            {
                                await _onExit(exitCode);
                            }
                            break;

                        case "foreground":
                            if (root.TryGetProperty("process", out var processProp)
                                && processProp.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                            {
                                var foreground = new PtyForeground(processProp.GetString());
                                Foreground = foreground;
                                if (_onForeground != null)
                                    await _onForeground(foreground);
                            }
                            break;

                        case "error":
                            var message = root.GetProperty("message").GetString();
                            Console.WriteLine($"[NodePty] Error: {message}");
                            break;

                        case "pong":
                            if (root.TryGetProperty("id", out var idProp))
                            {
                                var pongId = idProp.GetString();
                                if (pongId != null && _pendingPings.TryGetValue(pongId, out var tcs))
                                {
                                    tcs.TrySetResult(true);
                                }
                            }
                            break;
                    }
                }
                catch (JsonException)
                {
                    // Not JSON, might be stderr leak
                    Console.WriteLine($"[NodePty] Non-JSON: {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NodePty] Read error: {ex.Message}");
        }
    }

    /// <summary>Output already masked: kept for the replayed history and sent.</summary>
    private async Task EmitOutputAsync(string data)
    {
        if (data.Length == 0) return;
        _outputBuffer.Write(data);
        if (_onOutput != null)
        {
            await _onOutput(data);
        }
    }

    /// <summary>Reads the helper's stderr until it closes, logging the first lines (bounded) and discarding the rest.</summary>
    private static async Task DrainStderrAsync(StreamReader stderr)
    {
        var reader = new BoundedLineReader(stderr, MaxStderrLineLength);
        var logged = 0;
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (logged == MaxStderrLinesLogged) continue;
                logged++;
                Console.WriteLine(logged == MaxStderrLinesLogged
                    ? $"[NodePty] stderr: {MaxStderrLinesLogged} lines logged, the rest is discarded"
                    : $"[NodePty] stderr: {(line.TooLong ? $"[line longer than {MaxStderrLineLength} characters omitted]" : line.Text)}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The helper was stopped and its streams disposed.
        }
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            _isStopping = true;
        }

        if (_nodeProcess != null)
        {
            try
            {
                // Send stop command
                var cmd = new { type = "stop" };
                await SendCommandAsync(cmd, CancellationToken.None);

                // Give it time to clean up
                await Task.Delay(200);

                if (!_nodeProcess.HasExited)
                {
                    _nodeProcess.Kill(true);
                }
            }
            catch
            {
                // Ignore errors during stop
            }

            _nodeProcess.Dispose();
            _nodeProcess = null;
        }

        lock (_lock)
        {
            _hasExited = true;
        }

        if (_readTask != null)
        {
            try
            {
                await _readTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore
            }
        }

        _outputBuffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>
/// What runs in the foreground of a terminal (pty-helper's <c>foreground</c>): <paramref name="Process"/> is null while
/// the shell waits at its prompt, else the name of the program it runs (never its arguments).
/// </summary>
public sealed record PtyForeground(string? Process);
