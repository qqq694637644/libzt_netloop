using System.Text.Json;

namespace NetLoop.Core;

public static class JsonLog
{
    private static readonly object Gate = new();

    public static void Write(string level, string eventName, object? data = null)
    {
        var payload = new Dictionary<string, object?> {
            ["ts"] = DateTimeOffset.UtcNow,
            ["level"] = level,
            ["event"] = eventName,
            ["data"] = data
        };

        var line = JsonSerializer.Serialize(payload);
        lock (Gate)
        {
            Console.Out.WriteLine(line);
        }
    }

    public static void Info(string eventName, object? data = null) => Write("info", eventName, data);

    public static void Error(string eventName, object? data = null) => Write("error", eventName, data);
}
