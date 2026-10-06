using Android.Content;

namespace NetLoop.Android;

internal static class AndroidRuntimePaths
{
    internal static string GetStateDirectory(Context context)
        => Path.Combine(
            context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException(
                "Android FilesDir is unavailable."),
            "netloop-state");
}
