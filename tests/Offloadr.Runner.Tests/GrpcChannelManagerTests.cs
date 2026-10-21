using System.Buffers.Binary;
using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class GrpcChannelManagerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Dispose_ClosesOwnedHttp2Connection(bool traceHttp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.Use(next => async connection =>
            {
                Interlocked.Increment(ref connections);
                using var registration = connection.ConnectionClosed.Register(() => closed.TrySetResult());
                try { await next(connection); }
                finally { exited.TrySetResult(); }
            });
        }));
        await using var app = builder.Build();
        app.MapPost("/" + RunnerControlService.Descriptor.FullName + "/GrantPromptExecution", async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            context.Response.ContentType = "application/grpc";
            context.Response.DeclareTrailer("grpc-status");
            var message = new GrantPromptExecutionResponse { MayExecute = false }.ToByteArray();
            var frame = new byte[5 + message.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), message.Length);
            message.CopyTo(frame, 5);
            await context.Response.Body.WriteAsync(frame, context.RequestAborted);
            context.Response.AppendTrailer("grpc-status", "0");
        });
        await app.StartAsync(timeout.Token);
        using var channel = GrpcChannelManager.CreateChannel(app.Urls.Single(), traceHttp);
        try
        {
            var client = new RunnerControlService.RunnerControlServiceClient(channel);
            using var call = client.GrantPromptExecutionAsync(new GrantPromptExecutionRequest(),
                deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: timeout.Token);
            var response = await call.ResponseAsync;
            Assert.That(response.MayExecute, Is.False);
            Assert.That(connections, Is.EqualTo(1));
            Assert.That(closed.Task.IsCompleted, Is.False, "The completed RPC must leave a live pooled connection for the disposal check.");

            channel.Dispose();
            // The peer is still running: its shutdown must not make this assertion pass.
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cleanup.Token);
            // Do not let collection of an unreachable channel close the connection for us.
            GC.KeepAlive(channel);
        }
    }

    [TestCase("https://api.example.test", "https://api.example.test")]
    [TestCase("https://user:secret@api.example.test:8443/base?token=secret", "https://api.example.test:8443")]
    [TestCase("http://control-plane:31080/", "http://control-plane:31080")]
    [TestCase("not a url", "(unparsable url)")]
    public void RedactForLog_KeepsOnlySchemeHostAndPort(string url, string expected)
    {
        Assert.That(GrpcChannelManager.RedactForLog(url), Is.EqualTo(expected));
    }
}
