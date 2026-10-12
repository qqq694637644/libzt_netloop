using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NetLoop.Core;
using NetLoop.Host;
using NetLoop.Socks;

namespace NetLoop.Checks;

/// <summary>Deterministic offline checks: never start libzt or use a real identity.</summary>
internal static class Program
{
    private static int _passed;
    private static readonly IPAddress A = IPAddress.Parse("10.70.0.1");
    private static readonly IPAddress B = IPAddress.Parse("10.70.0.2");
    private static readonly IPAddress C = IPAddress.Parse("10.70.0.3");

    private static async Task<int> Main()
    {
        try
        {
            Check("Controller direct routing and B fail-closed", RouteDecisions);
            Check("Atomic route generation and invalidation", RouteGenerations);
            Check("Strict single Managed IPv4", SingleAddress);
            Check("Managed IP change requires exactly one rebuild", ManagedIpTransition);
            Check("UDP association removal matches key and object", UdpConditionalRemove);
            Check("SOCKS5 UDP datagram logical source", UdpPacketRoundTrip);
            Check("Shared UDP byte budget", UdpBudget);
            await CheckAsync("C Managed IP to 127.0.0.1 TCP", LoopbackTcp);
            await CheckAsync("C Managed IP to local UDP with source rewrite", LoopbackUdp);
            await CheckAsync("UDP Factory concurrent disposal", UdpFactoryDisposal);
            await CheckAsync("Optional status output failure isolation", StatusOutputFailure);
            await CheckAsync("JSONL consumer survives an output failure", JsonLogConsumerSurvives);
            Console.WriteLine($"PASS {_passed} offline tests");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL after {_passed} checks: {ex}");
            return 1;
        }
    }

    private static OverlayNetworkSnapshot Snapshot(
        IPAddress self, bool connected = true, bool via = false)
    {
        return new OverlayNetworkSnapshot(
            self,
            [self],
            connected ? [OverlayPrefix.Parse("10.70.0.0/24")] : [],
            via
                ? [new ManagedOverlayRoute(
                    OverlayPrefix.Parse("10.70.0.2/32"),
                    IPAddress.Parse("10.70.0.254"), 0, 0)]
                : []);
    }

    private static void RouteDecisions()
    {
        var router = new RouteSelector(Snapshot(A), B, 1);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.OverlayPeer, "A must route C directly");
        Require(router.Select(new ProxyTarget("example.org", 443), false).Kind
            == RouteKind.DefaultExit, "Internet must use B when directly reachable");
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), true).Kind
            == RouteKind.Reject, "Overlay ingress must never transit to C");

        var onC = new RouteSelector(Snapshot(C), B, 1);
        Require(onC.Select(new ProxyTarget(C.ToString(), 3389), true).Kind
            == RouteKind.LocalLoopback, "C must classify its own Managed IP as loopback");

        var withoutB = new RouteSelector(Snapshot(A, connected: false), B, 1);
        Require(withoutB.Select(new ProxyTarget("example.org", 80), false).Kind
            == RouteKind.Reject, "B without Controller route must reject Internet traffic");

        var withGateway = new RouteSelector(Snapshot(A, via: true), B, 1);
        Require(withGateway.Select(new ProxyTarget("example.org", 80), false).Kind
            == RouteKind.Reject, "Gateway via route must not be treated as peer Agent");
    }

    private static void RouteGenerations()
    {
        var router = new RouteSelector(Snapshot(A), B, 10);
        router.MarkNetworkUnavailable("controller_offline");
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.Reject, "Unavailable config must fail closed");

        router.UpdateNetwork(Snapshot(A), 10);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.Reject, "Same generation must not undo invalidation");
        router.UpdateNetwork(Snapshot(A), 9);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.Reject, "Old generation must not resurrect CIDR");
        router.UpdateNetwork(Snapshot(A), 11);
        Require(router.Select(new ProxyTarget(C.ToString(), 3389), false).Kind
            == RouteKind.OverlayPeer, "New Controller snapshot must restore routing");
        Require(router.Generation == 11, "Generation must be 11");
    }

    private static void SingleAddress()
    {
        Require(NetLoopRuntime.ValidateSingleManagedAddress([C]).Equals(C),
            "Single IPv4 was rejected");
        Throws<InvalidOperationException>(
            () => NetLoopRuntime.ValidateSingleManagedAddress([]));
        Throws<InvalidOperationException>(
            () => NetLoopRuntime.ValidateSingleManagedAddress([A, C]));
        Throws<InvalidOperationException>(
            () => NetLoopRuntime.ValidateSingleManagedAddress([IPAddress.IPv6Loopback]));
    }

    private static void ManagedIpTransition()
    {
        Require(!RuntimeOwner.RequiresManagedIpRebind(C, [C]),
            "Same IPv4 address must not restart Runtime");
        Require(RuntimeOwner.RequiresManagedIpRebind(C, [B]),
            "Revoked IP must request one Runtime replacement");
        Require(RuntimeOwner.RequiresManagedIpRebind(C, []),
            "Missing IP must withdraw active Runtime");
    }

    private static void UdpConditionalRemove()
    {
        var dictionary = new ConcurrentDictionary<string, object>();
        var old = new object();
        var replacement = new object();
        dictionary.TryAdd("peer", old);
        dictionary["peer"] = replacement;

        Require(!OverlayUdpAgent.RemoveMatching(dictionary, "peer", old),
            "Old association accidentally removed newer association");
        Require(dictionary.TryGetValue("peer", out var current)
                && ReferenceEquals(current, replacement),
            "Unexpected change to newer association");
        Require(OverlayUdpAgent.RemoveMatching(dictionary, "peer", replacement),
            "Current association could not be removed");
    }

    private static void UdpPacketRoundTrip()
    {
        var original = new ProxyTarget(C.ToString(), 3389);
        var raw = Socks5UdpPacket.Build(original, new byte[] { 12, 34, 56 });
        Require(Socks5UdpPacket.TryParse(raw, out var parsed, out var error),
            $"Cannot parse UDP packet: {error}");
        Require(parsed.Target == original, "UDP packet target changed");
        Require(parsed.Content.Span.SequenceEqual(new byte[] { 12, 34, 56 }),
            "UDP packet body changed");
    }

    private static void UdpBudget()
    {
        var budget = new UdpByteBudget(100);
        Require(budget.TryReserve(90), "Initial reservation failed");
        Require(!budget.TryReserve(11), "Exceeded byte limit");
        budget.Release(90);
        Require(budget.TryReserve(100), "Budget was not returned");
        budget.Release(100);
        Require(budget.UsedBytes == 0 && budget.PeakBytes == 100,
            "Budget counters inaccurate");

        var shared = new UdpByteBudget(1024);
        Parallel.For(0, 1000, _ => {
            if (shared.TryReserve(128))
                shared.Release(128);
        });
        Require(shared.UsedBytes == 0 && shared.PeakBytes <= 1024,
            "Concurrent reserve exceeded capacity or leaked bytes");
    }

    private static async Task LoopbackTcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = checked((ushort)((IPEndPoint)listener.LocalEndpoint).Port);
            var target = new ProxyTarget(C.ToString(), port);
            var route = new RouteSelector(Snapshot(C), B, 1);
            Require(route.Select(target, true).Kind == RouteKind.LocalLoopback,
                "C local TCP route was not loopback");
            var connector = new LoopbackTcpConnector(TimeSpan.FromSeconds(2));
            var accept = listener.AcceptTcpClientAsync();
            await using var connected = await connector.ConnectAsync(
                target, CancellationToken.None);
            using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(2));
            var actual = (IPEndPoint)accepted.Client.LocalEndPoint!;
            Require(actual.Address.Equals(IPAddress.Loopback)
                && actual.Port == port, "TCP destination was not 127.0.0.1:original port");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task LoopbackUdp()
    {
        var transport = new EchoUdpTransport();
        var reply = new TaskCompletionSource<ProxyUdpDatagram>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var association = new OverlayUdpAssociation(
            new IPEndPoint(A, 54321),
            C,
            new RouteSelector(Snapshot(C), B, 1),
            transport,
            new EchoUdpFactory(),
            TimeSpan.FromMinutes(1),
            32,
            TimeSpan.FromSeconds(2),
            new UdpByteBudget(1024 * 1024),
            (_, datagram, _) => {
                reply.TrySetResult(datagram);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await association.ForwardAsync(
            new ProxyTarget(C.ToString(), 3389),
            new byte[] { 99, 88 }, CancellationToken.None);
        var actual = await reply.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(transport.LastTarget == new ProxyTarget("127.0.0.1", 3389),
            "UDP was not forwarded to local loopback with original port");
        Require(actual.Source == new ProxyTarget(C.ToString(), 3389),
            "UDP reply logical source not rewritten to C Managed IP");
        var wire = Socks5UdpPacket.Build(actual.Source, actual.Content);
        Require(Socks5UdpPacket.TryParse(wire, out var parsed, out _)
            && parsed.Target == new ProxyTarget(C.ToString(), 3389),
            "SOCKS5 reply header does not identify C:original port");
    }

    private static async Task UdpFactoryDisposal()
    {
        using var control = new FakeControl();
        var blocking = new BlockingUdpFactory();
        var factory = new Socks5UdpAssociationFactory(
            blocking, 1, TimeSpan.FromMinutes(1));

        var creation = factory.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None).AsTask();
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstDispose = factory.DisposeAsync().AsTask();
        var secondDispose = factory.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Require(creation.IsCompleted, "Pending creation did not stop");
        try
        {
            await creation.ConfigureAwait(false);
            throw new Exception("Creation should have canceled");
        }
        catch (OperationCanceledException)
        {
        }

        var activeFactory = new Socks5UdpAssociationFactory(
            new EchoUdpFactory(), 2, TimeSpan.FromMinutes(1));
        var association = await activeFactory.CreateAsync(
            control, new ProxyTarget("0.0.0.0", 0), CancellationToken.None);
        await Task.WhenAll(
            activeFactory.DisposeAsync().AsTask(),
            activeFactory.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(3));
        await association.DisposeAsync();
    }

    private static async Task StatusOutputFailure()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "netloop-status-failure-" + Guid.NewGuid().ToString("N"));
        // The parent is a regular file, not a directory. CreateDirectory
        // must fail, and optional status output must swallow that failure.
        File.WriteAllText(root, "dummy file blocking status directory");
        try
        {
            await StatusWriter.TryWriteAsync(
                Path.Combine(root, "status.json"),
                new { phase = "ready" },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(root);
        }
    }

    private static async Task JsonLogConsumerSurvives()
    {
        var original = Console.Out;
        try
        {
            Console.SetOut(new FailOnceWriter(original));
            JsonLog.Info("synthetic_json_output_error");
            JsonLog.Info("synthetic_after_output_error");
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(3));
            await JsonLog.FlushAsync(deadline.Token);
        }
        finally
        {
            Console.SetOut(original);
        }

        JsonLog.Info("synthetic_consumer_still_alive");
        using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await JsonLog.FlushAsync(flush.Token);
    }

    private sealed class FailOnceWriter(TextWriter next) : TextWriter
    {
        private int _failed;
        public override System.Text.Encoding Encoding => next.Encoding;
        public override void WriteLine(string? value)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
                throw new IOException("synthetic stdout failure");
            next.WriteLine(value);
        }
        public override void Flush() => next.Flush();
    }

    private static void Check(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static async Task CheckAsync(string name, Func<Task> action)
    {
        await action().WaitAsync(TimeSpan.FromSeconds(8));
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private sealed class EchoUdpFactory : IProxyUdpTransportFactory
    {
        public ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IProxyUdpTransport>(new EchoUdpTransport());
        }
    }

    private sealed class EchoUdpTransport : IProxyUdpTransport
    {
        private readonly Channel<ProxyUdpDatagram> _replies =
            Channel.CreateUnbounded<ProxyUdpDatagram>();
        public ProxyTarget? LastTarget { get; private set; }

        public ValueTask SendAsync(
            ProxyTarget target,
            ReadOnlyMemory<byte> content,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastTarget = target;
            _replies.Writer.TryWrite(
                new ProxyUdpDatagram(target, content.ToArray()));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ProxyUdpDatagram> ReceiveAsync(CancellationToken token)
            => await _replies.Reader.ReadAsync(token);

        public ValueTask DisposeAsync()
        {
            _replies.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingUdpFactory : IProxyUdpTransportFactory
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unexpected completion");
        }
    }

    private sealed class FakeControl : IProxyConnection, IDisposable
    {
        public string Description => "synthetic-control";
        public EndPoint LocalEndPoint =>
            new IPEndPoint(IPAddress.Loopback, 45678);
        public EndPoint RemoteEndPoint =>
            new IPEndPoint(IPAddress.Loopback, 45679);

        public ValueTask<int> ReadAsync(byte[] b, int c, CancellationToken t) =>
            ValueTask.FromResult(0);
        public ValueTask WriteAsync(byte[] b, int c, CancellationToken t) =>
            ValueTask.CompletedTask;
        public ValueTask ShutdownWriteAsync(CancellationToken t) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
