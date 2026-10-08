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
