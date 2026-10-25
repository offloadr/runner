namespace Offloadr.Runner.Tests;

public partial class RunnerStartupValidatorTests
{
    [TestCase(38, 0)]
    [TestCase(1, 0)]
    [TestCase(0, 38)]
    [TestCase(0, 1)]
    [TestCase(0, 0)]
    public void ValidateOrThrow_RequiresPermittedProcessSignalsBeforeAcceptingWork(int openError, int signalError)
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), $"runner-signal-probe-{Guid.NewGuid():N}");
        var signals = 0;
        var closed = new List<int>();
        try
        {
            var options = BuildOptions(root, "/bin/sh");
            var validator = new RunnerStartupValidator(new NoopIsolationStrategy(), "/etc/passwd", null, null,
                () => SessionProcessSignal.GetUnsupportedReason(
                    () => openError == 0 ? 73 : -1,
                    descriptor => { Assert.That(descriptor, Is.EqualTo(73)); signals++; return signalError == 0 ? 0 : -1; },
                    closed.Add,
                    () => openError != 0 ? openError : signalError));
            if (openError == 0 && signalError == 0)
                Assert.DoesNotThrow(() => validator.ValidateOrThrow(options));
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() => validator.ValidateOrThrow(options));
                Assert.That(error!.Message, Does.Contain(openError != 0 ? "requires pidfd_open" : "requires pidfd_send_signal"));
                Assert.That(error.Message, Does.Contain($"errno={(openError != 0 ? openError : signalError)}"));
            }
            Assert.That(signals, Is.EqualTo(openError == 0 ? 1 : 0));
            Assert.That(closed, Is.EqualTo(openError == 0 ? new[] { 73 } : Array.Empty<int>()));
        }
        finally { TryDelete(root); }
    }

    [Test]
    public void ProcessSignalSupport_ProbesTheCurrentAgentWithoutDeliveringASignal()
    {
        LinuxTestPrerequisites.RequireLinux();
        Assert.That(SessionProcessSignal.GetUnsupportedReason(), Is.Null);
    }
}
