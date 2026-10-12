using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Supervisor.Tests;

public class UpdateCheckerTests
{
    private static readonly Version Current = new(1, 2, 3);

    [Test]
    public async Task ANewerRelease_IsOffered_WithItsReleasePage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v1.3.0"));
        using var checker = Create(handler);
        var changed = 0;
        checker.Changed += () => changed++;

        var update = await checker.CheckAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(update, Is.EqualTo(new AvailableUpdate(new Version(1, 3, 0), "https://github.com/offloadr/runner/releases/tag/v1.3.0")));
            Assert.That(checker.Available, Is.EqualTo(update));
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(handler.Request!.RequestUri, Is.EqualTo(new Uri(UpdateChecker.LatestReleaseUrl)));
            Assert.That(handler.Request.Headers.UserAgent.ToString(), Does.StartWith("OffloadrRunner/1.2.3"));
        });
    }

    [TestCase("v1.2.3")]
    [TestCase("1.2.0")]
    [TestCase("v2.0.0-beta.1")]
    [TestCase("nightly")]
    public async Task TheSameOlderOrUnparsableVersion_IsNotOffered(string tag)
    {
        using var checker = Create(new StubHandler(HttpStatusCode.OK, Release(tag)));

        Assert.That(await checker.CheckAsync(CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task DraftsAndPreReleases_AreNotOffered()
    {
        using var draft = Create(new StubHandler(HttpStatusCode.OK, Release("v9.0.0", draft: true)));
        using var prerelease = Create(new StubHandler(HttpStatusCode.OK, Release("v9.0.0", prerelease: true)));

        Assert.Multiple(async () =>
        {
            Assert.That(await draft.CheckAsync(CancellationToken.None), Is.Null);
            Assert.That(await prerelease.CheckAsync(CancellationToken.None), Is.Null);
        });
    }

    [Test]
    public async Task NoReleasesYet_MeansNoNotice()
    {
        using var checker = Create(new StubHandler(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        Assert.That(await checker.CheckAsync(CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task AFailedCheck_KeepsTheLastKnownAnswer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v1.4.0"));
        using var checker = Create(handler);
        await checker.CheckAsync(CancellationToken.None);

        handler.Throw = new HttpRequestException("offline");
        var update = await checker.CheckAsync(CancellationToken.None);

        Assert.That(update?.Version, Is.EqualTo(new Version(1, 4, 0)));
    }

    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public async Task ARateLimitOrServerError_KeepsTheLastKnownAnswer(HttpStatusCode status)
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v1.4.0"));
        using var checker = Create(handler);
        await checker.CheckAsync(CancellationToken.None);
        var changes = 0;
        checker.Changed += () => changes++;

        handler.Status = status;
        var update = await checker.CheckAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(update?.Version, Is.EqualTo(new Version(1, 4, 0)));
            Assert.That(checker.Available?.Version, Is.EqualTo(new Version(1, 4, 0)));
            Assert.That(changes, Is.Zero);
        });
    }

    [Test]
    public async Task AConnectionLostWhileReadingTheAnswer_KeepsTheLastKnownAnswer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v1.4.0"));
        using var checker = Create(handler);
        await checker.CheckAsync(CancellationToken.None);

        handler.BodyFailure = new IOException("The response ended prematurely.");
        var update = await checker.CheckAsync(CancellationToken.None);

        Assert.That(update?.Version, Is.EqualTo(new Version(1, 4, 0)));
    }

    [Test]
    public async Task BackgroundService_ChecksAfterTheInitialDelay()
    {
        using var checker = new UpdateChecker(Current, NullLogger<UpdateChecker>.Instance,
            new StubHandler(HttpStatusCode.OK, Release("v1.5.0")), TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        checker.Changed += () => changed.TrySetResult();

        await checker.StartAsync(CancellationToken.None);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await checker.StopAsync(CancellationToken.None);

        Assert.That(checker.Available?.Version, Is.EqualTo(new Version(1, 5, 0)));
    }

    [TestCase("0.1.0+9a681a2", 0, 1, 0)]
    [TestCase("v2.3", 2, 3, 0)]
    [TestCase("1.2.3.4", 1, 2, 3)]
    public void TryParseVersion_NormalizesToThreeParts(string text, int major, int minor, int build)
    {
        Assert.Multiple(() =>
        {
            Assert.That(UpdateChecker.TryParseVersion(text, out var version), Is.True);
            Assert.That(version, Is.EqualTo(new Version(major, minor, build)));
        });
    }

    private static UpdateChecker Create(HttpMessageHandler handler) => new(Current, NullLogger<UpdateChecker>.Instance, handler);

    private static string Release(string tag, bool draft = false, bool prerelease = false)
        => $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/offloadr/runner/releases/tag/{{tag}}",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}}
            }
            """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public HttpStatusCode Status { get; set; } = status;

        /// <summary>Thrown while the body is read, after the headers arrived.</summary>
        public Exception? BodyFailure { get; set; }

        public Exception? Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (Throw is not null)
            {
                throw Throw;
            }

            HttpContent content = BodyFailure is { } failure ? new FailingContent(failure) : new StringContent(body);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
        }
    }

    private sealed class FailingContent(Exception failure) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.FromException(failure);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new FailingStream(failure));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
