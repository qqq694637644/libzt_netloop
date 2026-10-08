using System.Text.Json;

namespace NetLoop.Host;

internal static class StatusWriter
{
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

        var temp = fullPath + ".tmp";
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temp, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        File.Move(temp, fullPath, overwrite: true);
    }
}
