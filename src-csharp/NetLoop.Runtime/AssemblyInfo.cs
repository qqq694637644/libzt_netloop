using System.Runtime.CompilerServices;

// Shared runtime implementation stays internal to its desktop, Android and
// standalone offline-check consumers. No public compatibility API is retained.
[assembly: InternalsVisibleTo("netloop")]
[assembly: InternalsVisibleTo("NetLoop.Android")]
[assembly: InternalsVisibleTo("NetLoop.Checks")]
