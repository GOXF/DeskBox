using System.Security.Cryptography;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class AppUpdateService
{
    /// <summary>No bytes for this long fails the source (it used to sit at 0 B forever).</summary>
    internal static TimeSpan ForkDownloadStallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private async Task<(long BytesReceived, long? TotalBytes, string Sha256)> ForkDownloadToFileAsync(
        Uri source,
        string tempPath,
        long manifestSize,
        IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stallCts.CancelAfter(ForkDownloadStallTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(
                source,
                HttpCompletionOption.ResponseHeadersRead,
                stallCts.Token);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is null && manifestSize > 0)
            {
                totalBytes = manifestSize;
            }

            progress?.Report(new AppUpdateDownloadProgress(0, totalBytes));
            long bytesReceived = 0;
            await using var remoteStream = await response.Content.ReadAsStreamAsync(stallCts.Token);
            await using var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 128];
            while (true)
            {
                stallCts.CancelAfter(ForkDownloadStallTimeout);
                int bytesRead = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token);
                if (bytesRead <= 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                hash.AppendData(buffer, 0, bytesRead);
                bytesReceived += bytesRead;
                progress?.Report(new AppUpdateDownloadProgress(bytesReceived, totalBytes));
            }

            await fileStream.FlushAsync(cancellationToken);
            return (bytesReceived, totalBytes, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No data from {source.Host} for {ForkDownloadStallTimeout.TotalSeconds:0} seconds.");
        }
    }
}
