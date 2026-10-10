using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Offloadr.Common.V1;
using Offloadr.EditorRuntime.V1;

namespace Offloadr.Runner.Core;

/// <summary>Retains complete native terminal SSE data without changing the relayed bytes.</summary>
internal sealed class ForgePromptEvidenceReader(string sessionHash, int maxBytes = ComfyRuntimeTransportLimits.MaxResponseBodyBytes) : IDisposable
{
    private readonly MemoryStream line = new();
    private readonly MemoryStream data = new();
    private bool afterCarriageReturn;

    public IReadOnlyList<PromptExecutionEvidence> Append(ReadOnlySpan<byte> chunk)
    {
        var results = new List<PromptExecutionEvidence>();
        foreach (var value in chunk)
        {
            if (afterCarriageReturn)
            {
                afterCarriageReturn = false;
                if (value == '\n') continue;
            }
            if (value is (byte)'\r' or (byte)'\n')
            {
                ReadLine(results);
                afterCarriageReturn = value == '\r';
            }
            else
            {
                if (line.Length + data.Length >= maxBytes)
                    throw new InvalidOperationException("Native SSE evidence exceeds the retained response limit.");
                line.WriteByte(value);
            }
        }
        return results;
    }

    private void ReadLine(List<PromptExecutionEvidence> results)
    {
        if (line.Length == 0)
        {
            if (data.Length > 0)
            {
                // SSE joins data lines with a newline and removes the final one.
                data.SetLength(data.Length - 1);
                try
                {
                    using var document = JsonDocument.Parse(data.GetBuffer().AsMemory(0, checked((int)data.Length)));
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("msg", out var message) && message.ValueKind == JsonValueKind.String && message.GetString() == "process_completed"
                        && root.TryGetProperty("event_id", out var id) && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()) && id.GetString()!.Length <= 256
                        && root.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        results.Add(new()
                        {
                            Kind = PromptEvidenceKind.ForgeTerminalEvent,
                            NativeIdentifiers = new() { SessionHash = sessionHash, EventId = id.GetString()! },
                            NativePayload = ByteString.CopyFrom(data.GetBuffer(), 0, checked((int)data.Length)),
                            ObservedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                        });
                }
                catch (JsonException) { }
                data.SetLength(0);
            }
        }
        else
        {
            var bytes = line.GetBuffer().AsSpan(0, checked((int)line.Length));
            if (bytes.StartsWith("data:"u8))
            {
                var value = bytes[5..];
                if (!value.IsEmpty && value[0] == ' ') value = value[1..];
                if (data.Length + value.Length + 1 > maxBytes)
                    throw new InvalidOperationException("Native SSE evidence exceeds the retained response limit.");
                data.Write(value);
                data.WriteByte((byte)'\n');
            }
        }
        line.SetLength(0);
    }

    public void Dispose()
    {
        line.Dispose();
        data.Dispose();
    }
}
