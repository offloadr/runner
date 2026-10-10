namespace Offloadr.Runner.Tests;

public class VfsIpcLogTextTests
{
    [Test]
    public void Sanitize_EscapesControlAndFormattingCharacters()
    {
        var rightToLeftOverride = (char)0x202E;
        var sanitized = VfsIpcLogText.Sanitize($"/models/a\nERROR forged\r\u001b[31mred{rightToLeftOverride}txt\\x\u0000");

        Assert.Multiple(() =>
        {
            Assert.That(sanitized, Is.EqualTo(@"/models/a\u000aERROR forged\u000d\u001b[31mred\" + "u202e" + @"txt\\x\u0000"));
            Assert.That(sanitized.Any(char.IsControl), Is.False);
        });
    }

    [Test]
    public void Sanitize_BoundsLengthAndReportsWhatWasCut()
    {
        var sanitized = VfsIpcLogText.Sanitize(new string('a', 10_000), maxLength: 64);

        Assert.Multiple(() =>
        {
            Assert.That(sanitized, Does.StartWith(new string('a', 64)));
            Assert.That(sanitized, Does.EndWith("...(+9936 chars)"));
            Assert.That(sanitized.Length, Is.LessThan(100));
            Assert.That(VfsIpcLogText.Sanitize(null), Is.Empty);
            Assert.That(VfsIpcLogText.Sanitize("/models/plain.bin"), Is.EqualTo("/models/plain.bin"));
        });
    }

    [Test]
    public void LogRateLimiter_AllowsBurstPerWindowAndReportsSuppressedCount()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new LogRateLimiter(maxPerWindow: 3, TimeSpan.FromMinutes(1), () => now);

        var allowed = Enumerable.Range(0, 10).Count(_ => limiter.TryAcquire(out _));
        now = now.AddSeconds(30);
        var stillLimited = limiter.TryAcquire(out _);
        now = now.AddSeconds(31);
        var afterWindow = limiter.TryAcquire(out var suppressed);

        Assert.Multiple(() =>
        {
            Assert.That(allowed, Is.EqualTo(3));
            Assert.That(stillLimited, Is.False);
            Assert.That(afterWindow, Is.True);
            Assert.That(suppressed, Is.EqualTo(8));
        });
    }
}
