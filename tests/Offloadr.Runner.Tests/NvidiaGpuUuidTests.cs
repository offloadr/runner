namespace Offloadr.Runner.Tests;

public class NvidiaGpuUuidTests
{
    [TestCase("GPU-00000000-1111-2222-3333-444444444444", "GPU-00000000-1111-2222-3333-444444444444")]
    [TestCase(" gpu-AAAAAAAA-1111-2222-3333-444444444444 ", "GPU-aaaaaaaa-1111-2222-3333-444444444444")]
    public void TryNormalize_GivesNvidiaSpelling(string text, string expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(NvidiaGpuUuid.TryNormalize(text, out var uuid), Is.True);
            Assert.That(uuid, Is.EqualTo(expected));
        });
    }

    [TestCase(null)]
    [TestCase("  ")]
    [TestCase("GPU-0000")]
    [TestCase("00000000-1111-2222-3333-444444444444")]
    [TestCase("GPU-{00000000-1111-2222-3333-444444444444}")]
    [TestCase("MIG-00000000-1111-2222-3333-444444444444")]
    public void TryNormalize_RefusesAnythingButACompleteGpuUuid(string? text)
    {
        Assert.Multiple(() =>
        {
            Assert.That(NvidiaGpuUuid.TryNormalize(text, out var uuid), Is.False);
            Assert.That(uuid, Is.Null);
        });
    }
}
