using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WSA.Core.Interfaces;
using WSA.Core.Models;

namespace WSA.Infrastructure.ProcessExecution;

/// <summary>
/// Direct (shell-free) process execution. Arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/>,
/// output is read with asynchronous line readers on both streams (no deadlock), and cancellation/timeouts kill only
/// the process tree started by the call.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxCapturedChars = 2 * 1024 * 1024;
    private static readonly char[] BatchUnsafeChars = { '&', '|', '<', '>', '^', '%', '!', '"', '\r', '\n' };

    public async Task<CommandResult> RunAsync(
        CommandRequest request,
        IProgress<OutputLine>? output = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        CommandResult Failed(string error) => new(
            request.Executable, request.Arguments, null, string.Empty, string.Empty,
            stopwatch.Elapsed, false, error);

        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            return Failed("No executable specified.");
        }

        if (!File.Exists(request.Executable))
        {
            return Failed($"Executable not found: {request.Executable}");
        }

        // .bat/.cmd files are interpreted by cmd.exe even without a shell on our side, so reject metacharacters
        // that cmd.exe would reinterpret instead of passing through as plain arguments.
        var ext = Path.GetExtension(request.Executable);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var bad = request.Arguments.FirstOrDefault(a => a.IndexOfAny(BatchUnsafeChars) >= 0);
            if (bad is not null)
            {
                return Failed(
                    $"Argument '{bad}' contains characters that cmd.exe would interpret (& | < > ^ % ! \"), " +
                    "and this tool is a .bat file. Remove them or use a different invocation.");
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // cmd.exe splits unquoted batch arguments at ';' ',' and '='. SDK package ids such as
            // "system-images;android-34;google_apis;x86_64" would be torn apart, so every argument is quoted.
            // Arguments containing '"' or '%' were rejected above, which keeps this quoting unambiguous.
            psi.Arguments = string.Join(' ', request.Arguments.Select(QuoteForBatch));
        }
        else
        {
            foreach (var arg in request.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            psi.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return Failed("The process did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Failed($"Could not start {Path.GetFileName(request.Executable)}: {ex.Message}");
        }

        // Send the optional scripted input, then always close stdin so tools never wait on it.
        try
        {
            if (!string.IsNullOrEmpty(request.StandardInput))
            {
                await process.StandardInput.WriteAsync(request.StandardInput).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process may already have exited or closed its input; nothing to do.
        }

        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t)
        {
            timeoutCts.CancelAfter(t);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = PumpAsync(process.StandardOutput, OutputKind.StandardOutput, stdout, output);
        var stderrTask = PumpAsync(process.StandardError, OutputKind.StandardError, stderr, output);

        var cancelled = false;
        string? startError = null;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                startError = $"Timed out after {timeout!.Value.TotalSeconds:0} s and was terminated.";
            }

            TryKillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Streams reach EOF once the process (and any children holding the pipes) have exited.
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Exit code unavailable; leave null.
        }

        stopwatch.Stop();
        return new CommandResult(
            request.Executable, request.Arguments, exitCode,
            stdout.ToString(), stderr.ToString(), stopwatch.Elapsed, cancelled, startError);
    }

    public async Task<BinaryResult> RunBinaryAsync(
        CommandRequest request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        const int maxBytes = 32 * 1024 * 1024;

        if (!File.Exists(request.Executable))
        {
            return new BinaryResult(null, Array.Empty<byte>(), string.Empty, $"Executable not found: {request.Executable}", false);
        }

        var ext = Path.GetExtension(request.Executable);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return new BinaryResult(null, Array.Empty<byte>(), string.Empty,
                "Binary capture is only supported for real executables, not batch files.", false);
        }

        var psi = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            psi.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new BinaryResult(null, Array.Empty<byte>(), string.Empty, "The process did not start.", false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new BinaryResult(null, Array.Empty<byte>(), string.Empty,
                $"Could not start {Path.GetFileName(request.Executable)}: {ex.Message}", false);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Already exited.
        }

        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t)
        {
            timeoutCts.CancelAfter(t);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var buffer = new MemoryStream();
        var stdoutTask = Task.Run(async () =>
        {
            var chunk = new byte[81920];
            var stream = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stream.ReadAsync(chunk, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read <= maxBytes)
                {
                    buffer.Write(chunk, 0, read);
                }
            }
        });
        var stderrTask = process.StandardError.ReadToEndAsync();

        var cancelled = false;
        string? error = null;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                error = $"Timed out after {timeout!.Value.TotalSeconds:0} s and was terminated.";
            }

            TryKillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Unavailable.
        }

        return new BinaryResult(exitCode, buffer.ToArray(), stderrTask.Result, error, cancelled);
    }

    private static string QuoteForBatch(string arg)
    {
        // A backslash run directly before the closing quote would escape it, so double those backslashes.
        var trailing = arg.Length - arg.TrimEnd('\\').Length;
        return "\"" + arg + new string('\\', trailing) + "\"";
    }

    private static async Task PumpAsync(StreamReader reader, OutputKind kind, StringBuilder sink, IProgress<OutputLine>? progress)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (sink.Length < MaxCapturedChars)
            {
                sink.AppendLine(line);
            }

            progress?.Report(new OutputLine(kind, line));
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        catch (Win32Exception)
        {
            // Access denied or process already gone; WaitForExit below will resolve either way.
        }
    }
}
