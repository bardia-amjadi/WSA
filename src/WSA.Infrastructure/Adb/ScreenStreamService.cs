using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using WSA.Core.Interfaces;
using WSA.Core.Models;
using WSA.Infrastructure.Configuration;
using WSA.Infrastructure.Setup;

namespace WSA.Infrastructure.Adb;

/// <summary>
/// H.264 screen stream through the scrcpy server (Apache-2.0, https://github.com/Genymobile/scrcpy). The server jar is
/// downloaded once from the official release and checked against the SHA-256 published for it, then pushed to the phone and
/// started with adb. WSA connects to it through an adb forward and reads the raw video; nothing but adb is used on the phone.
/// </summary>
public sealed class ScreenStreamService : IScreenStreamService
{
    private const string ReleaseApi = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
    private const string RemoteJar = "/data/local/tmp/wsa-scrcpy-server.jar";
    private static readonly Regex Sha256Regex = new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);

    private readonly IToolCommandFactory _tools;
    private readonly IProcessRunner _runner;
    private readonly FileDownloader _downloader;
    private readonly IAppLog _log;

    public ScreenStreamService(IToolCommandFactory tools, IProcessRunner runner, FileDownloader downloader, IAppLog log)
    {
        _tools = tools;
        _runner = runner;
        _downloader = downloader;
        _log = log;
    }

    public async Task<IScreenStreamSession> StartAsync(
        string serial, ScreenStreamOptions options, IProgress<string> status, CancellationToken ct = default)
    {
        var (jar, version) = await EnsureServerAsync(status, ct).ConfigureAwait(false);

        status.Report("Sending the screen server to the phone...");
        var push = await AdbAsync(serial, new[] { "push", jar, RemoteJar }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!push.Succeeded)
        {
            throw new ToolFailedException($"Could not copy the screen server to the phone: {push.StartError ?? ToolFirst(push)}");
        }

        var port = FreePort();
        var scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");
        var forward = await AdbAsync(serial, new[] { "forward", $"tcp:{port}", $"localabstract:scrcpy_{scid}" }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!forward.Succeeded)
        {
            throw new ToolFailedException($"Could not set up the adb forward: {forward.StartError ?? ToolFirst(forward)}");
        }

        var serverCts = new CancellationTokenSource();
        var serverOutput = new ConcurrentQueue<string>();
        var serverArgs = new List<string>
        {
            "shell", $"CLASSPATH={RemoteJar}", "app_process", "/", "com.genymobile.scrcpy.Server", version,
            $"scid={scid}", "log_level=info", "audio=false", "control=false", "tunnel_forward=true", "raw_stream=true",
            $"video_bit_rate={options.BitrateMbps * 1_000_000}", $"max_size={options.MaxSize}", $"max_fps={options.Fps}",
        };
        var request = Create(serial, serverArgs);
        _log.Debug("stream", $"starting scrcpy server {version} on port {port}, scid {scid}");

        var progress = new SyncProgress(line =>
        {
            serverOutput.Enqueue(line.Text);
            while (serverOutput.Count > 30)
            {
                serverOutput.TryDequeue(out _);
            }

            _log.Debug("stream", "server: " + line.Text);
        });
        var serverTask = Task.Run(() => _runner.RunAsync(request, progress, null, serverCts.Token));

        status.Report("Starting the screen stream...");
        TcpClient? client = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        try
        {
            while (client is null)
            {
                ct.ThrowIfCancellationRequested();
                if (serverTask.IsCompleted)
                {
                    throw new ToolFailedException("The screen server stopped: " + (serverOutput.IsEmpty ? "no output" : string.Join(" | ", serverOutput.TakeLast(5))));
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new ToolFailedException("The phone did not start the screen stream in time.");
                }

                var attempt = new TcpClient { NoDelay = true };
                try
                {
                    await attempt.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                    var probe = new byte[1];
                    var read = attempt.GetStream().ReadAsync(probe, 0, 1, ct);
                    if (await Task.WhenAny(read, Task.Delay(1500, ct)).ConfigureAwait(false) == read && await read.ConfigureAwait(false) == 1)
                    {
                        // One byte was consumed to prove the server is alive. It is either a dummy byte or a zero of the first
                        // start code, and the video reader skips to the next start code either way.
                        client = attempt;
                        continue;
                    }
                }
                catch (SocketException)
                {
                    // The server is not listening yet.
                }
                catch (IOException)
                {
                    // Closed before the server was ready.
                }

                attempt.Dispose();
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await StopAsync(serverCts, serverTask, port, serial).ConfigureAwait(false);
            client?.Dispose();
            throw;
        }

        return new Session(client, version, async () => await StopAsync(serverCts, serverTask, port, serial).ConfigureAwait(false));
    }

    private async Task StopAsync(CancellationTokenSource serverCts, Task serverTask, int port, string serial)
    {
        serverCts.Cancel();
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The adb process is killed with its tree; nothing more to wait for.
        }

        try
        {
            await AdbAsync(serial, new[] { "forward", "--remove", $"tcp:{port}" }, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ToolFailedException or ToolUnavailableException)
        {
            // A stale forward is harmless and disappears when adb restarts.
        }

        serverCts.Dispose();
    }

    private async Task<(string Path, string Version)> EnsureServerAsync(IProgress<string> status, CancellationToken ct)
    {
        var dir = Path.Combine(ManagedPaths.AppDataDir, "scrcpy");
        var info = Path.Combine(dir, "server.json");
        if (File.Exists(info))
        {
            try
            {
                using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(info, ct).ConfigureAwait(false));
                var file = cached.RootElement.GetProperty("file").GetString();
                var cachedVersion = cached.RootElement.GetProperty("version").GetString();
                if (file is not null && cachedVersion is not null && File.Exists(Path.Combine(dir, file)))
                {
                    return (Path.Combine(dir, file), cachedVersion);
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException or InvalidOperationException)
            {
                // Fall through and download again.
            }
        }

        status.Report("Downloading the screen server (scrcpy, about 90 KB, one time)...");
        var json = await _downloader.GetStringAsync(new Uri(ReleaseApi), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new ToolFailedException("The scrcpy release has no version tag.");
        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.StartsWith("scrcpy-server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.GetProperty("browser_download_url").GetString()!;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha = d["sha256:".Length..];
            }

            sha ??= FindHashInNotes(body, name);
            if (sha is null || !Sha256Regex.IsMatch(sha))
            {
                throw new ToolFailedException(
                    $"GitHub did not publish a SHA-256 for {name}, so WSA will not use it. Use 'Compatible' live view in Settings instead.");
            }

            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, name);
            await _downloader.DownloadAsync(new Uri(url), target, "SHA256", sha, (_, _) => { }, ct).ConfigureAwait(false);
            var version = tag.TrimStart('v');
            await File.WriteAllTextAsync(info, JsonSerializer.Serialize(new { file = name, version, sha256 = sha }), ct).ConfigureAwait(false);
            return (target, version);
        }

        throw new ToolFailedException($"The scrcpy release {tag} has no server file.");
    }

    private static string? FindHashInNotes(string notes, string assetName)
    {
        foreach (var line in notes.Split('\n'))
        {
            if (line.Contains(assetName, StringComparison.OrdinalIgnoreCase))
            {
                var m = Sha256Regex.Match(line);
                if (m.Success)
                {
                    return m.Value;
                }
            }
        }

        return null;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private CommandRequest Create(string serial, IReadOnlyList<string> args)
    {
        var full = new List<string> { "-s", serial };
        full.AddRange(args);
        return _tools.TryCreate(SdkTool.Adb, full, out var error)
               ?? throw new ToolUnavailableException(error ?? "adb is unavailable.");
    }

    private Task<CommandResult> AdbAsync(string serial, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        _runner.RunAsync(Create(serial, args), null, timeout, ct);

    private static string ToolFirst(CommandResult r) =>
        WSA.Core.Services.ToolOutputParsers.FirstLine(r.StandardError + "\n" + r.StandardOutput) ?? $"exit code {r.ExitCode}";

    private sealed class SyncProgress : IProgress<OutputLine>
    {
        private readonly Action<OutputLine> _handler;

        public SyncProgress(Action<OutputLine> handler) => _handler = handler;

        public void Report(OutputLine value) => _handler(value);
    }

    private sealed class Session : IScreenStreamSession
    {
        private readonly TcpClient _client;
        private readonly Func<Task> _stop;
        private int _disposed;

        public Session(TcpClient client, string version, Func<Task> stop)
        {
            _client = client;
            _stop = stop;
            ServerVersion = version;
            Video = client.GetStream();
        }

        public Stream Video { get; }

        public string ServerVersion { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _client.Dispose();
            await _stop().ConfigureAwait(false);
        }
    }
}
