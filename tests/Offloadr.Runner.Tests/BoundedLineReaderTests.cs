using System.Threading.Channels;

namespace Offloadr.Runner.Tests;

public class BoundedLineReaderTests
{
    [Test]
    public async Task PumpAsync_SplitsLinesOnLfCrAndCrLf_KeepingEmptyLines()
    {
        var lines = new List<string>();

        await BoundedLineReader.PumpAsync(new StringReader("a\r\nb\rc\n\nd"), lines.Add);

        Assert.That(lines, Is.EqualTo(new[] { "a", "b", "c", "", "d" }));
    }

    [Test]
    public async Task PumpAsync_SplitsOverlongLine_AndMarksContinuations()
    {
        var lines = new List<string>();
        var longLine = new string('x', 2_500);

        await BoundedLineReader.PumpAsync(new StringReader(longLine + "\nnext\n"), lines.Add, maxLineChars: 1_000);

        Assert.That(lines, Is.EqualTo(new[]
        {
            new string('x', 1_000),
            BoundedLineReader.ContinuationPrefix + new string('x', 1_000),
            BoundedLineReader.ContinuationPrefix + new string('x', 500),
            "next",
        }));
    }

    [Test]
    public async Task PumpAsync_NewlineRightAfterSplit_DoesNotEmitEmptyContinuation()
    {
        var lines = new List<string>();

        await BoundedLineReader.PumpAsync(new StringReader(new string('x', 1_000) + "\ny"), lines.Add, maxLineChars: 1_000);

        Assert.That(lines, Is.EqualTo(new[] { new string('x', 1_000), "y" }));
    }

    [Test]
    public async Task PumpAsync_DoesNotSplitSurrogatePairs()
    {
        var lines = new List<string>();
        var text = "a" + string.Concat(Enumerable.Repeat("\U0001F600", 10));

        await BoundedLineReader.PumpAsync(new StringReader(text), lines.Add, maxLineChars: 4);

        Assert.That(lines, Has.All.Matches<string>(line => !char.IsHighSurrogate(line[^1])));
        Assert.That(
            string.Concat(lines).Replace(BoundedLineReader.ContinuationPrefix, string.Empty, StringComparison.Ordinal),
            Is.EqualTo(text));
    }

    [Test]
    public async Task PumpAsync_DeliversPiecesOfAnUnterminatedLineBeforeTheStreamEnds()
    {
        var reader = new ChunkedReader();
        var lines = new List<string>();
        var pump = BoundedLineReader.PumpAsync(reader, line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        }, maxLineChars: 1_000);

        for (var i = 0; i < 10; i++)
        {
            await reader.WriteAsync(new string('z', 512));
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines)
            {
                if (lines.Count >= 5)
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        Assert.That(pump.IsCompleted, Is.False);
        lock (lines)
        {
            Assert.That(lines, Has.Count.EqualTo(5));
        }

        reader.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(lines, Has.Count.EqualTo(6));
    }

    [Test]
    public async Task PumpAsync_KeepsReadingWhenTheConsumerThrows()
    {
        var calls = 0;

        await BoundedLineReader.PumpAsync(new StringReader("a\nb\n"), _ =>
        {
            calls++;
            throw new InvalidOperationException("consumer failure");
        });

        Assert.That(calls, Is.EqualTo(2));
    }

    private sealed class ChunkedReader : TextReader
    {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        private string _current = string.Empty;
        private int _offset;

        public ValueTask WriteAsync(string chunk) => _chunks.Writer.WriteAsync(chunk);

        public void Complete() => _chunks.Writer.Complete();

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                _current = await _chunks.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }
}
