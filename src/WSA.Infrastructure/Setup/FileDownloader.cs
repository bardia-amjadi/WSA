using System.Security.Cryptography;
using System.Net.Http.Headers;
using WSA.Core.Models;

namespace WSA.Infrastructure.Setup;

/// <summary>HTTPS-only downloader with streaming progress and mandatory hash verification.</summary>
public sealed class FileDownloader : IDisposable
{
    private readonly HttpClient _http;

    public FileDownloader()
    {
        // SocketsHttpHandler never follows an https -> http redirect, so a downgrade cannot happen silently.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WSA", "0.1"));
    }

    public async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
    {
        RequireHttps(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            return await _http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolFailedException($"Timed out contacting {url.Host}. Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            throw new ToolFailedException($"Could not reach {url.Host}: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads to <paramref name="destination"/> via a ".partial" file and only keeps the file if its hash equals
    /// <paramref name="expectedHex"/>. <paramref name="algorithm"/> is "SHA1" or "SHA256".
    /// </summary>
    public async Task DownloadAsync(
        Uri url, string destination, string algorithm, string expectedHex,
        Action<long, long?> report, CancellationToken cancellationToken)
    {
        RequireHttps(url);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial";

        using var hash = IncrementalHash.CreateHash(
            algorithm.Equals("SHA1", StringComparison.OrdinalIgnoreCase) ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            RequireHttps(response.RequestMessage?.RequestUri ?? url);

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    report(done, total);
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolFailedException(
                    $"Checksum mismatch for {url.Host}{url.AbsolutePath}: expected {expectedHex.Trim()}, got {actual}. " +
                    "The download was discarded and nothing was installed.");
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolFailedException($"Download from {url.Host} failed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(partial))
            {
                try
                {
                    File.Delete(partial);
                }
                catch (IOException)
                {
                    // A leftover .partial file is harmless and replaced on the next attempt.
                }
            }
        }
    }

    private static void RequireHttps(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ToolFailedException($"Refusing non-HTTPS download: {url}");
        }
    }

    public void Dispose() => _http.Dispose();
}
