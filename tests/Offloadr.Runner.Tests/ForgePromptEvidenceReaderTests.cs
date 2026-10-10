using System.Text;
using Offloadr.Common.V1;

namespace Offloadr.Runner.Tests;

public sealed class ForgePromptEvidenceReaderTests
{
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(65536)]
    public void RetainsExactUnicodeTerminalDataAcrossChunkAndCrlfBoundaries(int chunkSize)
    {
        const string payload = """{"msg":"process_completed","event_id":"event-1","success":true,"output":{"text":"雪🌻"}}""";
        var bytes = Encoding.UTF8.GetBytes(": heartbeat\r\ndata: " + payload + "\r\n\r\n");
        using var reader = new ForgePromptEvidenceReader("captured-session");
        var evidence = new List<PromptExecutionEvidence>();
        for (var index = 0; index < bytes.Length; index += chunkSize)
            evidence.AddRange(reader.Append(bytes.AsSpan(index, Math.Min(chunkSize, bytes.Length - index))));
        Assert.That(evidence, Has.Count.EqualTo(1));
        Assert.That(evidence[0].Kind, Is.EqualTo(PromptEvidenceKind.ForgeTerminalEvent));
        Assert.That(evidence[0].NativeIdentifiers.SessionHash, Is.EqualTo("captured-session"));
        Assert.That(evidence[0].NativeIdentifiers.EventId, Is.EqualTo("event-1"));
        Assert.That(evidence[0].NativePayload.ToStringUtf8(), Is.EqualTo(payload));
    }

    [Test]
    public void IgnoresProgressMalformedAndIncompleteFramesWithoutManufacturingTerminalState()
    {
        using var reader = new ForgePromptEvidenceReader("session");
        var evidence = reader.Append(Encoding.UTF8.GetBytes("data: not-json\n\ndata: {\"msg\":\"progress\"}\n\ndata: {\"msg\":\"process_completed\",\"event_id\":\"partial\",\"success\":true}"));
        Assert.That(evidence, Is.Empty);
    }

    [Test]
    public void MultipleDataLinesPreserveTheNativePayloadNewlineAndFailureFlag()
    {
        using var reader = new ForgePromptEvidenceReader("session");
        var evidence = reader.Append("data: {\"msg\":\"process_completed\",\rdata: \"event_id\":\"event-1\",\"success\":false}\r\r"u8);
        Assert.That(evidence, Has.Count.EqualTo(1));
        Assert.That(evidence[0].NativePayload.ToStringUtf8(), Is.EqualTo("{\"msg\":\"process_completed\",\n\"event_id\":\"event-1\",\"success\":false}"));
    }

    [Test]
    public void OversizedFrameStopsRetentionInsteadOfTruncatingEvidence()
    {
        using var reader = new ForgePromptEvidenceReader("session", maxBytes: 32);
        Assert.Throws<InvalidOperationException>(() => reader.Append(Encoding.UTF8.GetBytes("data: " + new string('x', 40))));
    }
}
