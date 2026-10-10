using System.Net;
using Offloadr.EditorRuntime.V1;

namespace Offloadr.Runner.Tests;

public class EditorResponseLimitTests
{
    [Test]
    public async Task ReadBoundedStringAsync_ReturnsASmallResponse()
    {
        using var client = new HttpClient(new StreamHandler(() => new MemoryStream("{\"dependencies\":[]}"u8.ToArray())));

        var body = await SessionProcessManager.ReadBoundedStringAsync(client, new Uri("http://127.0.0.1/config"), CancellationToken.None);

        Assert.That(body, Is.EqualTo("{\"dependencies\":[]}"));
    }

    [Test]
    public void ReadBoundedStringAsync_StopsAtTheLimitWithoutBufferingTheRest()
    {
        var stream = new EndlessStream();
        using var client = new HttpClient(new StreamHandler(() => stream));

        Assert.That(
            async () => await SessionProcessManager.ReadBoundedStringAsync(client, new Uri("http://127.0.0.1/config"), CancellationToken.None),
            Throws.InvalidOperationException);
        Assert.That(stream.BytesRead, Is.LessThanOrEqualTo(ComfyRuntimeTransportLimits.MaxResponseBodyBytes + (64 * 1024)));
    }

    [TestCase(HttpStatusCode.OK, true)]
    [TestCase(HttpStatusCode.NotFound, false)]
    [TestCase(HttpStatusCode.ServiceUnavailable, false)]
    public async Task ProbeRuntimeAsync_IsReadyOnlyOnASuccessfulResponse(HttpStatusCode status, bool ready)
    {
        var root = Path.Combine(Path.GetTempPath(), $"runner-ready-{Guid.NewGuid():N}");
        try
        {
            var options = new SessionProcessOptions
            {
                SessionRoot = Path.Combine(root, "sessions"),
                EntryPointPath = Path.Combine(root, "entrypoint.sh"),
                WorkingDirectory = root,
                BundledCustomNodesSeedPath = Path.Combine(root, "seed-custom_nodes"),
                BundledInputSeedPath = Path.Combine(root, "seed-input"),
                ComfyPort = 8188,
                ReadyHost = "127.0.0.1",
                ReadyPath = "/system_stats",
                ShutdownGracePeriod = TimeSpan.FromSeconds(1),
                ReadyTimeout = TimeSpan.FromSeconds(1),
            };
            using var manager = new SessionProcessManager(
                options,
                new LinuxUserIsolationStrategy(new LinuxCommandRunner()),
                new RunnerVfsEnvironmentBuilder(),
                new LinuxCommandRunner(),
                httpMessageHandler: new StatusHandler(status));

            Assert.That(await manager.ProbeRuntimeAsync(CancellationToken.None), Is.EqualTo(ready));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class StreamHandler(Func<Stream> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) });
    }

    /// <summary>A response body that never ends, as a misbehaving endpoint might send.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            BytesRead += count;
            return count;
        }
    }
}
