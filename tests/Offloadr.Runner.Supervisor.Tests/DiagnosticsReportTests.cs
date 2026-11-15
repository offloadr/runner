using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hardware;
using Offloadr.Runner.Supervisor.Hosting;
using Offloadr.Runner.Supervisor.LaunchPlans;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Tests;

public class DiagnosticsReportTests
{
    private const string Key = "settings-key-0123456789abcdefghijklmnopqrstuvwxyz";
    private const string RunningKey = "running-key-0123456789abcdefghijklmnopqrstuvwxyz";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 30, 5, TimeSpan.Zero);

    private static DiagnosticsInput Input(
        SupervisorView? view = null,
        RunnerSettings? settings = null,
        IReadOnlyList<string>? logLines = null,
        IReadOnlyList<DiagnosticLogFile>? diagnosticLogs = null)
        => new(
            "1.4.0+abc123",
            "Microsoft Windows 10.0.26100 X64",
            view ?? SupervisorView.Initial(Now),
            settings ?? new RunnerSettings { RunnerKey = Key, LocalModelsDirectory = @"C:\Users\alex\models" },
            logLines ?? [],
            diagnosticLogs ?? [],
            Now)
        {
            OtherRunnerKeys = [RunningKey],
            UserProfile = @"C:\Users\alex",
            UserName = "alex",
        };

    private static SupervisorView Described() => SupervisorView.Initial(Now) with
    {
        State = SupervisorState.Faulted,
        Message = "The runner needs attention.",
        Engine = new ContainerEngineInfo(true, ContainerEngineKind.Docker, @"C:\Program Files\Docker\docker.exe", "28.1.0", "linux", "Docker Desktop", true, null),
        HostGpus = new HostGpuReport([new HostGpu("NVIDIA GeForce RTX 4090", 24564)], null),
        GpuAccess = new GpuAccessCheck(false, "could not select device driver \"\" with capabilities: [[gpu]]"),
        ActiveTemplate = new LaunchTemplate("comfyui-latest", "comfyui", "ghcr.io/offloadr/runner-comfyui:latest", "ComfyUI"),
        Image = new ImageInfo("sha256:1111", "ghcr.io/offloadr/runner-comfyui@sha256:2222"),
        RunnerStatus = new RunnerStatusSnapshot("runner-7", "instance-3", "1.4.0", ["comfyui-latest"], "session-9", Now),
        Restarts = 2,
        Fault = new SupervisorFault(FaultKind.GpuUnavailable, "The GPU is not available to Docker", "Docker could not give the runner the GPU.", "docker: Error response from daemon"),
    };

    [Test]
    public void Report_DescribesTheAppTheRunnerDockerTheGpuAndTheSettings()
    {
        var report = DiagnosticsReport.Build(Input(
            Described(),
            logLines: ["pull: Pulling fs layer", "supervisor: the runner stopped; restarting in 5 s."],
            diagnosticLogs: [new DiagnosticLogFile("diagnostic-2026111508.log", "2026-11-15T08:00:00Z severity=error component=supervisor errorClass=io\n")]));

        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("Version: 1.4.0+abc123"));
            Assert.That(report, Does.Contain("State: Needs attention (Faulted)"));
            Assert.That(report, Does.Contain("Problem: GpuUnavailable: The GPU is not available to Docker"));
            Assert.That(report, Does.Contain("    docker: Error response from daemon"));
            Assert.That(report, Does.Contain("Restarts: 2"));
            Assert.That(report, Does.Contain("Image digest: ghcr.io/offloadr/runner-comfyui@sha256:2222"));
            Assert.That(report, Does.Contain("Runner ID: runner-7"));
            Assert.That(report, Does.Contain("Session running: yes"));
            Assert.That(report, Does.Not.Contain("session-9"), "Whether a session runs is enough.");
            Assert.That(report, Does.Contain("Server version: 28.1.0"));
            Assert.That(report, Does.Contain("GPU: NVIDIA GeForce RTX 4090, 24564 MiB"));
            Assert.That(report, Does.Contain("GPU inside a container: failed"));
            Assert.That(report, Does.Contain("Runner key: set"));
            Assert.That(report, Does.Contain(@"Models folder: %USERPROFILE%\models"));
            Assert.That(report, Does.Contain("Keep awake during sessions: yes"));
            Assert.That(report, Does.Contain("== Runner log (2 recent lines)"));
            Assert.That(report, Does.Contain("supervisor: the runner stopped; restarting in 5 s."));
            Assert.That(report, Does.Contain("-- diagnostic-2026111508.log\n2026-11-15T08:00:00Z severity=error").Or.Contain("-- diagnostic-2026111508.log\r\n2026-11-15T08:00:00Z severity=error"));
        });
    }

    [Test]
    public void Report_NeverHoldsEitherRunnerKey_WhereverItAppears()
    {
        var view = SupervisorView.Initial(Now) with
        {
            Engine = new ContainerEngineInfo(false, ContainerEngineKind.Docker, "docker", null, null, null, false, $"exec failed: RUNNER_SECRET={Key}"),
            GpuAccess = new GpuAccessCheck(false, $"env RUNNER_SECRET={RunningKey}"),
        };
        var report = DiagnosticsReport.Build(Input(
            view with { Fault = new SupervisorFault(FaultKind.Unknown, "Failed", $"key {Key}", $"detail {RunningKey}") },
            logLines: [$"echo {Key}", $"old {RunningKey}"],
            diagnosticLogs: [new DiagnosticLogFile("diagnostic-2026111508.log", $"{Key}\n")]));

        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Not.Contain(Key));
            Assert.That(report, Does.Not.Contain(RunningKey));
            Assert.That(report, Does.Not.Contain("0123456789abcdefghijklmnop"), "No part of a key survives.");
            Assert.That(report, Does.Contain("echo [runner key]"));
            Assert.That(report, Does.Contain("old [runner key]"));
        });
    }

    [Test]
    public void Redact_RemovesAKeyThatContainsAnotherWhole()
    {
        var input = Input() with { OtherRunnerKeys = [Key + "-longer"] };

        Assert.That(DiagnosticsReport.Redact($"a {Key}-longer b {Key}", input), Is.EqualTo("a [runner key] b [runner key]"));
    }

    [TestCase("GET https://bucket.example/models/a.safetensors?X-Amz-Signature=abc&X-Amz-Expires=600 200",
        "GET https://bucket.example/models/a.safetensors?[removed] 200")]
    [TestCase("download \"https://cdn.example/f?token=s3cr3t\" failed", "download \"https://cdn.example/f?[removed]\" failed")]
    [TestCase("Authorization: Bearer eyJhbGciOi.payload.sig", "Authorization: Bearer [removed]")]
    [TestCase("authorization: bearer abc123, retrying", "authorization: Bearer [removed], retrying")]
    [TestCase("https://offloadr.studio/settings", "https://offloadr.studio/settings")]
    [TestCase("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Redact_RemovesBearerTokensAndTheQueryOfEveryLink(string line, string expected)
        => Assert.That(DiagnosticsReport.Redact(line, Input()), Is.EqualTo(expected));

    [TestCase(@"bind C:\Users\alex\models", @"bind %USERPROFILE%\models")]
    [TestCase(@"bind c:\users\ALEX\models", @"bind %USERPROFILE%\models")]
    [TestCase("bind C:/Users/alex/models", "bind %USERPROFILE%/models")]
    [TestCase("mount /run/desktop/mnt/host/c/Users/alex/models", "mount /run/desktop/mnt/host/c/Users/[user]/models")]
    [TestCase("mount /mnt/c/Users/alex", "mount /mnt/c/Users/[user]")]
    [TestCase("home /home/alex/.cache", "home /home/[user]/.cache")]
    [TestCase("other /mnt/c/Users/alexandra/models", "other /mnt/c/Users/alexandra/models")]
    public void Redact_ReplacesTheUserFolderAndNameInPaths(string line, string expected)
        => Assert.That(DiagnosticsReport.Redact(line, Input()), Is.EqualTo(expected));

    [TestCase("cache /root/.cache/models", "cache %USERPROFILE%/.cache/models")]
    [TestCase("home /root", "home %USERPROFILE%")]
    [TestCase("layer /rootfs/usr", "layer /rootfs/usr")]
    [TestCase("mount /run/root/x", "mount /run/root/x")]
    public void Redact_ReplacesALinuxHomeOnlyAsAWholeFolder(string line, string expected)
        => Assert.That(DiagnosticsReport.Redact(line, Input() with { UserProfile = "/root/", UserName = null }), Is.EqualTo(expected));

    [Test]
    public void Report_KeepsTheStartAndTheEndOfALongGpuCheck()
    {
        var output = "first line\n" + new string('x', DiagnosticsReport.MaximumCheckOutput * 2) + "\nlast line";
        var report = DiagnosticsReport.Build(Input(SupervisorView.Initial(Now) with { GpuAccess = new GpuAccessCheck(false, output) }));

        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("    first line"));
            Assert.That(report, Does.Contain("    last line"));
            Assert.That(report, Does.Contain("[…]"));
            Assert.That(report.Length, Is.LessThan(DiagnosticsReport.MaximumCheckOutput + 4096));
        });
    }

    [Test]
    public void Report_SaysWhichDiagnosticLogsCouldNotBeRead_AndWhatWasNotCheckedYet()
    {
        var report = DiagnosticsReport.Build(Input(
            settings: new RunnerSettings(),
            diagnosticLogs: [new DiagnosticLogFile("diagnostic-2026111507.log", null, "IOException")]));

        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("Could not be read: IOException"));
            Assert.That(report, Does.Contain("Runner key: not set"));
            Assert.That(report, Does.Contain("Problem: " + RunnerSettings.MissingKeyProblem));
            Assert.That(report, Does.Contain("== Docker" + Environment.NewLine + "Not checked yet."));
        });
    }

    [Test]
    public void FileName_NamesTheTimeItWasCreated()
        => Assert.That(DiagnosticsReport.FileName(Now), Is.EqualTo("offloadr-runner-diagnostics-20261115-093005.txt"));
}

public class DiagnosticsExportTests
{
    private string _logs = null!;

    [SetUp]
    public void SetUp()
    {
        _logs = Path.Combine(Path.GetTempPath(), "runner-diagnostics-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_logs);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_logs, recursive: true);

    [Test]
    public void ReadDiagnosticLogs_ReadsOnlyTheAppsOwnMetadataFiles_OldestFirst()
    {
        File.WriteAllText(Path.Combine(_logs, "diagnostic-2026111509.log"), "newer\n");
        File.WriteAllText(Path.Combine(_logs, "diagnostic-2026111508.log"), "older\n");
        File.WriteAllText(Path.Combine(_logs, "diagnostic-latest.log"), "not ours\n");
        File.WriteAllText(Path.Combine(_logs, "diagnostic-2026111508.log.bak"), "not ours\n");
        File.WriteAllText(Path.Combine(_logs, "runner-2026-11-01.log"), "an old log with content\n");
        File.WriteAllText(Path.Combine(_logs, "settings.json"), "{}");

        var files = DiagnosticsExport.ReadDiagnosticLogs(_logs);

        Assert.That(files.Select(file => (file.Name, file.Text)), Is.EqualTo(new[]
        {
            ("diagnostic-2026111508.log", "older\n"),
            ("diagnostic-2026111509.log", "newer\n"),
        }));
    }

    [Test]
    public void ReadDiagnosticLogs_KeepsTheNewestWhenTheyAreTooLarge()
    {
        var large = new string('n', DiagnosticsExport.MaximumDiagnosticLogBytes - 10);
        File.WriteAllText(Path.Combine(_logs, "diagnostic-2026111509.log"), large);
        File.WriteAllText(Path.Combine(_logs, "diagnostic-2026111508.log"), "older and cut short\n");
        File.WriteAllText(Path.Combine(_logs, "diagnostic-2026111507.log"), "oldest\n");

        var files = DiagnosticsExport.ReadDiagnosticLogs(_logs);

        Assert.Multiple(() =>
        {
            Assert.That(files.Select(file => file.Name), Is.EqualTo(new[] { "diagnostic-2026111507.log", "diagnostic-2026111508.log", "diagnostic-2026111509.log" }));
            Assert.That(files[0].Text, Is.Null);
            Assert.That(files[0].Error, Does.StartWith("left out"));
            Assert.That(files[1].Text, Is.EqualTo("older and "));
            Assert.That(files[2].Text, Is.EqualTo(large));
        });
    }

    [Test]
    public void ReadDiagnosticLogs_FindsNothingWithoutTheFolder()
        => Assert.That(DiagnosticsExport.ReadDiagnosticLogs(Path.Combine(_logs, "missing")), Is.Empty);
}
