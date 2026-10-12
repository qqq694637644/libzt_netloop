using System.Text.Json;
using NetLoop.Core;

namespace NetLoop.Host;

internal static class StatusWriter
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task TryWriteAsync(
        string? path,
        object status,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(path, status, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Optional diagnostics must not terminate the SOCKS data plane.
            JsonLog.Error("status_write_failed", new {
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
    }

    internal static async Task WriteAsync(
        string? path,
        object status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(
                status, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                temp, json + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            Gate.Release();
        }
    }
}
