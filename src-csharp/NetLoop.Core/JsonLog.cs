using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NetLoop.Core;

public static class JsonLog
{
    private const int QueueCapacity = 4096;
    private static readonly Channel<LogRecord> Queue = Channel.CreateBounded<LogRecord>(
        new BoundedChannelOptions(QueueCapacity) {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private static readonly Task Writer = Task.Run(DrainAsync);
    private static long _droppedInfo;
    private static long _droppedError;
    private static string? _filePath;
    private static long _fileMaxBytes;
    private static int _fileRetained;
    private static int _configured;

    /// <summary>
    /// Optional bounded disk logging. Call before starting libzt. The single
    /// background consumer handles all file I/O and rotation; native event
    /// callbacks never wait for the console or a slow disk.
    /// </summary>
    public static void ConfigureFile(string path, int maxMegabytes = 16, int retainedFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxMegabytes is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxMegabytes));
        if (retainedFiles is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        if (Interlocked.Exchange(ref _configured, 1) != 0)
            throw new InvalidOperationException("JSON log file has already been configured.");

        _fileMaxBytes = maxMegabytes * 1024L * 1024L;
        _fileRetained = retainedFiles;
        Volatile.Write(ref _filePath, Path.GetFullPath(path));
    }

    public static void Write(string level, string eventName, object? data = null)
    {
        var content = new Dictionary<string, object?> {
            ["ts"] = DateTimeOffset.UtcNow,
            ["level"] = level,
            ["event"] = eventName,
            ["data"] = data
        };

        var line = JsonSerializer.Serialize(content);
        if (!Queue.Writer.TryWrite(new LogRecord(line, null)))
        {
            // Never block the native event callback or data plane on output.
            // The writer emits aggregate counters when it next catches up.
            if (level == "error")
                Interlocked.Increment(ref _droppedError);
            else
                Interlocked.Increment(ref _droppedInfo);
        }
    }

    public static void Info(string eventName, object? data = null) => Write("info", eventName, data);

    public static void Error(string eventName, object? data = null) => Write("error", eventName, data);

    /// <summary>Drain all queued records written before the flush barrier.</summary>
    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var barrier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await Queue.Writer.WriteAsync(new LogRecord(null, barrier), cancellationToken)
            .ConfigureAwait(false);
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainAsync()
    {
        RotatingFileSink? sink = null;
        try
        {
            while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var processed = 0;
                while (processed++ < 256 && Queue.Reader.TryRead(out var record))
                {
                    try
                    {
                        if (record.Line is not null)
                            WriteLine(record.Line, ref sink);

                        if (record.Barrier is not null)
                        {
                            sink?.Flush();
                            Console.Out.Flush();
                            record.Barrier.TrySetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        record.Barrier?.TrySetException(ex);
                        OnWriterFault(ex, ref sink);
                    }
                }

                // A single loss summary per batch avoids turning heavy log
                // pressure into a storm of additional diagnostics.
                var droppedInfo = Interlocked.Exchange(ref _droppedInfo, 0);
                var droppedError = Interlocked.Exchange(ref _droppedError, 0);
                if (droppedInfo != 0 || droppedError != 0)
                {
                    try
                    {
                        WriteLine(JsonSerializer.Serialize(new {
                            ts = DateTimeOffset.UtcNow,
                            level = "error",
                            @event = "json_log_records_dropped",
                            data = new {
                                info = droppedInfo,
                                error = droppedError,
                                capacity = QueueCapacity
                            }
                        }), ref sink);
                    }
                    catch (Exception ex)
                    {
                        OnWriterFault(ex, ref sink);
                    }
                }

                try
                {
                    sink?.Flush();
                }
                catch (Exception ex)
                {
                    OnWriterFault(ex, ref sink);
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"NetLoop JSON log writer stopped: {ex}");
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                sink?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    private static void OnWriterFault(Exception ex, ref RotatingFileSink? sink)
    {
        try
        {
            sink?.Dispose();
        }
        catch (Exception)
        {
        }
        sink = null;
        // Do not continuously retry a broken disk path; use best-effort
        // stdout for future records. A broken stdout is also nonfatal.
        Volatile.Write(ref _filePath, null);
        try
        {
            Console.Error.WriteLine($"NetLoop log output failed: {ex.Message}");
        }
        catch (Exception)
        {
        }
    }

    private static void WriteLine(string line, ref RotatingFileSink? sink)
    {
        var configuredPath = Volatile.Read(ref _filePath);
        if (configuredPath is null)
        {
            Console.Out.WriteLine(line);
            return;
        }

        try
        {
            sink ??= new RotatingFileSink(configuredPath, _fileMaxBytes, _fileRetained);
            sink.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the configured disk sink must not stop the logger.
            // Fall back to stdout and make the disk failure visible.
            try
            {
                sink?.Dispose();
            }
            catch (IOException)
            {
            }
            sink = null;
            Volatile.Write(ref _filePath, null);
            Console.Error.WriteLine($"NetLoop log file failed; reverting to stdout: {ex.Message}");
            Console.Out.WriteLine(line);
        }
    }

    private sealed record LogRecord(string? Line, TaskCompletionSource? Barrier);

    private sealed class RotatingFileSink : IDisposable
    {
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _retained;
        private StreamWriter _writer;
        private long _bytes;

        internal RotatingFileSink(string path, long maxBytes, int retained)
        {
            _path = path;
            _maxBytes = maxBytes;
            _retained = retained;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = Open(path, out _bytes);
        }

        internal void WriteLine(string line)
        {
            var length = Encoding.UTF8.GetByteCount(line) + 1;
            if (_bytes > 0 && _bytes + length > _maxBytes)
                Rotate();

            _writer.WriteLine(line);
            _bytes += length;
        }

        internal void Flush() => _writer.Flush();

        private void Rotate()
        {
            _writer.Dispose();
            for (var index = _retained; index >= 1; index--)
            {
                var source = index == 1
                    ? _path : $"{_path}.{index - 1}";
                var destination = $"{_path}.{index}";
                if (File.Exists(source))
                    File.Move(source, destination, overwrite: true);
            }

            _writer = Open(_path, out _bytes);
        }

        private static StreamWriter Open(string path, out long length)
        {
            var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024);
            length = stream.Length;
            return new StreamWriter(stream, new UTF8Encoding(false)) {
                NewLine = "\n"
            };
        }

        public void Dispose() => _writer.Dispose();
    }
}
