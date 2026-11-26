using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.Supervisor.Containers;
using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Supervisor.Tests;

public class RunnerCleanupTests
{
    private const string OwnerId = "0123456789abcdef0123456789abcdef";

    private string _directory = string.Empty;
    private SupervisorPaths _paths = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "offloadr-cleanup-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        _paths = new SupervisorPaths(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task WithoutARememberedEngine_NothingRanSoNothingIsCalled()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        var processes = new FakeProcessRunner();

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(processes.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task TheRememberedRunnerOfThisDataFolder_IsRemovedAndForgotten()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId));

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(commands, Does.Contain("stop --time 10 abc"), "The agent gets to shut down cleanly first.");
            Assert.That(commands.IndexOf("stop --time 10 abc"), Is.LessThan(commands.IndexOf("rm --force abc")));
            Assert.That(File.Exists(_paths.EngineState), Is.False);
        });
    }

    [Test]
    public async Task ARunnerThatWillNotStop_IsStillRemoved()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId))
            .On("stop", 1, stderr: "Error response from daemon: cannot stop container: abc");

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(processes.Calls.Select(call => string.Join(' ', call.Arguments)), Does.Contain("rm --force abc"));
        });
    }

    [Test]
    public async Task ARunnerAnotherCopyStarted_IsLeftAlone()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect("fedcba9876543210fedcba9876543210"));

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(processes.Calls.Select(call => call.Arguments[0]), Does.Not.Contain("rm"));
        });
    }

    [Test]
    public async Task ARunnerAnotherRunningCopyManages_IsLeftAlone()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId));
        using var running = new SupervisorInstanceLock(_paths.SupervisorLock);
        Assert.That(running.TryAcquire(), Is.True);

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(processes.Calls, Is.Empty);
            Assert.That(File.Exists(_paths.EngineState), Is.True, "The running copy still needs its engine hint.");
        });
    }

    [Test]
    public async Task AnEngineThatHangs_DoesNotCostTheStoppedRunnersOnOthersTheirRemoval()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, Environment.ProcessPath!));
        File.WriteAllLines(_paths.LeftoverContainers, [$"{engine} hung", $"{engine} abc"]);
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false))
            .On("rm", 0);
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments.Contains("hung"))
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True, "It still held the runner key.");
            Assert.That(commands, Does.Contain("rm abc"));
            Assert.That(File.ReadAllText(_paths.LeftoverContainers).Trim(), Is.EqualTo($"{engine} hung"), "Tried again later.");
        });
    }

    [Test]
    public async Task AStoppedRunnerTheEngineKept_IsRemovedFromItsOwnEngine_NeverByForce()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LeftoverContainers, $"{ContainerEngineSelection.Describe(engine)} abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false))
            .On("rm", 0);

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True, "It still held the runner key.");
            Assert.That(commands, Does.Contain("rm abc"));
            Assert.That(commands, Has.None.Contains("--force"));
            Assert.That(File.Exists(_paths.LeftoverContainers), Is.False);
        });
    }

    [Test]
    public async Task ALaunchedRunnerStoppedByTheCleanup_IsReportedGone()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} -");
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: target));
        var reported = new List<RunnerCleanup.StoppedLaunch>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { new RunnerCleanup.StoppedLaunch(launchId, target) }),
                "The app is being removed: there may be no next start to tell Offloadr.");
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False, "Offloadr took the report.");
        });
    }

    [Test]
    public async Task ALaunchedRunnerItStopped_IsStillReportedGone_WhenItsRemovalHangs()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} -");
        Remember("abc");

        // It stops, and then its removal never returns.
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: target));
        var atRemoval = new List<string>();
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                atRemoval.Add(File.ReadAllText(_paths.LaunchedContainers));
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reported = new List<RunnerCleanup.StoppedLaunch>();
        var reportsBeforeRemoval = -1;
        using var installer = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), installer.Token, (stopped, _) =>
            {
                reportsBeforeRemoval = reportsBeforeRemoval < 0 ? atRemoval.Count : reportsBeforeRemoval;
                reported.Add(stopped);
                return Task.FromResult(true);
            }, within: TimeSpan.FromSeconds(5));
        }
        catch (ContainerEngineException)
        {
            // The removal did not return in its share of the time: told to the caller once everything else is done.
        }

        Assert.Multiple(() =>
        {
            Assert.That(processes.Calls.Select(call => call.Arguments[0]), Does.Contain("stop"));
            Assert.That(atRemoval.FirstOrDefault(), Does.StartWith($"gone {launchId} "), "Noted gone before its removal is tried.");
            Assert.That(File.Exists(_paths.LeftoverContainers) ? File.ReadAllText(_paths.LeftoverContainers) : string.Empty, Does.Contain(" abc"),
                "Stopped and not removed: noted, so a later start of the app removes it though its record is settled.");
            Assert.That(reportsBeforeRemoval, Is.EqualTo(1), "The report does not hold up the removal of a container that holds the runner key.");
            Assert.That(reported, Is.EqualTo(new[] { new RunnerCleanup.StoppedLaunch(launchId, target) }),
                "And the removal, which never returned, did not use up the time for the report.");
            Assert.That(installer.IsCancellationRequested, Is.False);
        });
    }

    [Test]
    public async Task ARunnerStillOnlyCreated_WhoseRemovalAndNextLookHang_DoesNotUseUpTheTimeForTheReports()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string gone = "7c5f0a1e-0b0c-4a52-9a53-0d3c9b6f1e11";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllLines(_paths.LaunchedContainers, [$"running {launchId} {target} -", $"gone {gone} {target} -"]);
        Remember("abc");

        // Found created, its start asked for. Its removal never returns, and nor does any look after that.
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal));
        var removalAsked = false;
        var looksAfterRemoval = 0;
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                removalAsked = true;
                await Task.Delay(Timeout.Infinite, token);
            }
            else if (removalAsked && spec.Arguments[0] == "container")
            {
                Interlocked.Increment(ref looksAfterRemoval);
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reported = new List<RunnerCleanup.StoppedLaunch>();
        var within = TimeSpan.FromSeconds(6);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan? reportedAfter = null;

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
            {
                reportedAfter ??= System.Diagnostics.Stopwatch.GetElapsedTime(began);
                reported.Add(stopped);
                return Task.FromResult(true);
            }, within: within);
        }
        catch (ContainerEngineException)
        {
            // The removal did not return in its share of the time: told to the caller once everything else is done.
        }

        Assert.Multiple(() =>
        {
            Assert.That(reported.Select(stopped => stopped.LaunchId), Is.EqualTo(new[] { gone }),
                "What is known gone is reported; the one that may be started yet is not.");
            Assert.That(reportedAfter, Is.LessThan(within), "The look after the removal did not use up the reports' share of the time.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers), Does.Contain($"running {launchId} "), "Its record stays as it is.");
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AGrantedRelease_IsNotedBeforeTheContainerIsStopped(bool remembered)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: target));
        var atStop = string.Empty;
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "stop" && atStop.Length == 0)
            {
                atStop = File.ReadAllText(_paths.LaunchedContainers);
            }

            return Task.CompletedTask;
        };
        var atAsking = string.Empty;
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(
            _paths,
            processes,
            new SupervisorOptions(),
            CancellationToken.None,
            (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                atAsking = atAsking.Length == 0 ? File.ReadAllText(_paths.LaunchedContainers) : atAsking;
                return Task.FromResult(true);
            });

        Assert.Multiple(() =>
        {
            Assert.That(atAsking, Does.StartWith($"asking {launchId} "), "A grant whose answer is lost still counts.");
            Assert.That(atStop, Does.StartWith($"released {launchId} "), "Cut short here, the next start of the app never takes it up again.");
            Assert.That(reports, Is.Zero, "Offloadr released it: it knows.");
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False);
        });
    }

    [Test]
    public async Task WhatIsAlreadyNotedGone_IsReported_ThoughTheRememberedEngineHangs()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"gone {launchId} {target} -");
        Remember("abc");

        // The engine the runner was started on never answers.
        var processes = new FakeProcessRunner
        {
            OnRun = (_, token) => Task.Delay(Timeout.Infinite, token),
        };
        var reported = new List<RunnerCleanup.StoppedLaunch>();
        using var installer = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), installer.Token, (stopped, _) =>
            {
                reported.Add(stopped);
                return Task.FromResult(true);
            });
        }
        catch (ContainerEngineException)
        {
            // The remembered engine did not answer: told to the caller once everything else is done.
        }

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { new RunnerCleanup.StoppedLaunch(launchId, target) }), "It needs no engine: told whatever the engine does.");
            Assert.That(installer.IsCancellationRequested, Is.False, "The engine that hangs does not use up the installer's time.");
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False);
        });
    }

    [Test]
    public async Task ARememberedRunnerSeenCreated_WhoseStartWasAsked_IsReportedGone()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} -");
        Remember("abc");

        // The app closed after asking for its start; it is found created (and may still have run since).
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal));
        var reported = new List<RunnerCleanup.StoppedLaunch>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        });

        Assert.That(reported, Is.EqualTo(new[] { new RunnerCleanup.StoppedLaunch(launchId, target) }),
            "Its start was asked for: Offloadr is told it is gone, rather than left to wait for it.");
    }

    [TestCase("refused")]
    [TestCase("starting")]
    public async Task ARememberedRunnerSeenCreated_WhoseStartWasRefusedOrNeverAsked_IsForgotten_AndNotReportedGone(string state)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"{state} {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        Remember("abc");

        // Seen created, and removed: a later look finds nothing there that would say it never ran.
        var processes = new FakeProcessRunner()
            .On("ps", 0, string.Empty)
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal));
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions { CancelledLaunchSettle = TimeSpan.FromMilliseconds(200) }, CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Zero, "It ran nothing: an exit report would cost the allocation a retry.");
            Assert.That(processes.Calls.Any(call => call.Arguments[0] == "rm"), Is.True);
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False, "Settled.");
        });
    }

    [Test]
    public async Task ARememberedRunnerWhoseStartIsStillUnderWay_IsReleasedAndStopped_WhenItRunsAsItsRemovalFails()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} -");
        Remember("abc");

        // Found created; the engine starts it after all, and then refuses its removal.
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal))
            .On("rm", 1, stderr: "Error response from daemon: removal of container abc is already in progress");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                processes.On("container", 0, Inspect(OwnerId, running: true, launchId: launchId, target: target));
            }

            return Task.CompletedTask;
        };
        var reports = 0;
        var releases = 0;

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            }, (_, _) =>
            {
                releases++;
                return Task.FromResult(false);
            });
        }
        catch (ContainerEngineException)
        {
            // The removal failed: told to the caller.
        }

        Assert.That(processes.Calls.Any(call => call.Arguments[0] == "stop"), Is.True, "It runs after all: stopped first, as any that runs, never removed by force.");
        Assert.Multiple(() =>
        {
            Assert.That(releases, Is.EqualTo(1), "It may serve by then: Offloadr is asked first.");
            Assert.That(reports, Is.EqualTo(1), "Stopped: it serves nothing any more.");
            Assert.That(processes.Calls.Select(call => string.Join(' ', call.Arguments)).First(call => call.StartsWith("rm", StringComparison.Ordinal)), Is.EqualTo("rm abc"), "Not by force while it was taken for created.");
            Assert.That(File.Exists(_paths.LeftoverContainers), Is.True, "Not removed: noted for later.");
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AContainerSeenCreated_ThatRanAndExitedAsItsRemovalFailed_IsReportedGone(bool remembered)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"starting {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        // Its start was never asked for here; someone started it as it was removed, and it exited.
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal))
            .On("rm", 1, stderr: "Error response from daemon: driver failed to remove the container");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                processes.On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"exited\",", StringComparison.Ordinal));
            }

            return Task.CompletedTask;
        };
        var reports = 0;

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions { CancelledLaunchSettle = TimeSpan.FromMilliseconds(200) }, CancellationToken.None, (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            });
        }
        catch (ContainerEngineException)
        {
            // The removal failed: told to the caller.
        }

        Assert.That(reports, Is.EqualTo(1), "The engine shows that it ran.");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AContainerSeenExited_ThatRunsAgainAsItIsRemoved_IsReleasedAndStopped_NotRemovedByForceFirst(bool remembered)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target))
            .On("rm", 1, stderr: "Error response from daemon: cannot remove container abc: container is running");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "stop")
            {
                processes.On("rm", 0);
            }

            return Task.CompletedTask;
        };
        var releases = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) => Task.FromResult(true), (_, _) =>
        {
            releases++;
            return Task.FromResult(false);
        });

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(releases, Is.EqualTo(1));
            Assert.That(commands.First(call => call.StartsWith("rm", StringComparison.Ordinal)), Is.EqualTo("rm abc"));
            Assert.That(commands.FindIndex(call => call.StartsWith("stop", StringComparison.Ordinal)), Is.GreaterThan(-1).And.LessThan(commands.IndexOf("rm --force abc")), "Stopped before it is removed.");
            Assert.That(File.Exists(_paths.LeftoverContainers), Is.False);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AContainerSeenExited_ThatRunsAgain_IsNotReportedGone_WhileItCannotBeStoppedOrRemoved(bool remembered)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        // Found exited by the look; it runs by the time it is removed, and the engine neither stops nor removes it.
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target))
            .On("rm", 1, stderr: "Error response from daemon: cannot remove container abc: container is running")
            .On("stop", 1, stderr: "Error response from daemon: cannot stop container abc");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                processes.On("container", 0, Inspect(OwnerId, running: true, launchId: launchId, target: target));
            }

            return Task.CompletedTask;
        };
        var reports = 0;

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            }, (_, _) => Task.FromResult(false));
        }
        catch (ContainerEngineException)
        {
            // Told to the caller.
        }

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Zero, "It runs: telling Offloadr it is gone would end its session.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers), Does.Contain(launchId).And.Not.Contain("gone "), "Looked for again.");
        });
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task AContainerSeenExited_ThatRunsAgain_IsNotReportedGone_ThoughItsRemovalWasNotAnswered(bool remembered, bool lookAnswered)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        // Found exited; it runs by the time it is removed, and the engine's answer to that is lost (nor is it stopped).
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target))
            .On("rm", 1, stderr: "error during connect: EOF")
            .On("stop", 1, stderr: "error during connect: EOF");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                if (lookAnswered)
                {
                    processes.On("container", 0, Inspect(OwnerId, running: true, launchId: launchId, target: target));
                }
                else
                {
                    processes.On("container", 1, stderr: "error during connect: EOF").On("ps", 1, stderr: "error during connect: EOF");
                }
            }

            return Task.CompletedTask;
        };
        var reports = 0;

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            }, (_, _) => Task.FromResult(false));
        }
        catch (ContainerEngineException)
        {
            // Told to the caller.
        }

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Zero, "It may serve: telling Offloadr it is gone would end its session.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers), Does.Contain(launchId).And.Not.Contain("gone "), "Looked for again.");
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AContainerSeenExited_ThatTurnsOutToRunAfterItsRemovalFailed_IsReleasedAndStopped(bool remembered)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        // Found exited; it runs by the time it is removed, which the engine does not say (nor whether it still
        // did at once after); asked once more, it does.
        var looks = 0;
        var removing = false;
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target))
            .On("rm", 1, stderr: "error during connect: EOF");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm" && !removing)
            {
                removing = true;
                processes.On("container", 1, stderr: "error during connect: EOF");
            }
            else if (spec.Arguments[0] == "container" && removing && ++looks == 2)
            {
                processes.On("container", 0, Inspect(OwnerId, running: true, launchId: launchId, target: target)).On("rm", 0);
            }

            return Task.CompletedTask;
        };
        var releases = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) => Task.FromResult(true), (_, _) =>
        {
            releases++;
            return Task.FromResult(false);
        });

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(releases, Is.EqualTo(1));
            Assert.That(commands, Has.Some.StartsWith("stop "));
            Assert.That(commands, Has.Some.StartsWith("rm --force "));
        });
    }

    [TestCase(true, "starting")]
    [TestCase(false, "starting")]
    [TestCase(true, "refused")]
    [TestCase(false, "refused")]
    public async Task AContainerSeenExited_IsNotedAsOneThatRan_ThoughItsRemovalWasNotAnswered(bool remembered, string state)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"{state} {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target))
            .On("rm", 1, stderr: "error during connect: EOF");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                processes.On("container", 1, stderr: "error during connect: EOF").On("ps", 1, stderr: "error during connect: EOF");
            }

            return Task.CompletedTask;
        };
        var reports = 0;

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            }, (_, _) => Task.FromResult(false));
        }
        catch (ContainerEngineException)
        {
            // Told to the caller.
        }

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Zero, "The engine does not say what became of it.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers), Does.StartWith($"running {launchId} "), "It ran: told as gone once it is.");
        });
    }

    [Test]
    public async Task AContainerFoundByItsRecordCreated_ThatRunsAsItIsRemoved_IsReleasedAndStopped()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: "target").Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal))
            .On("rm", 1, stderr: "Error response from daemon: cannot remove container abc: container is running");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "stop")
            {
                processes.On("rm", 0);
            }

            return Task.CompletedTask;
        };
        var releases = 0;
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        }, (_, _) =>
        {
            releases++;
            return Task.FromResult(false);
        });

        var commands = processes.Calls.Select(call => call.Arguments[0]).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(releases, Is.EqualTo(1));
            Assert.That(reports, Is.EqualTo(1));
            Assert.That(commands.IndexOf("stop"), Is.GreaterThan(-1).And.LessThan(commands.LastIndexOf("rm")), "Stopped before it is removed.");
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False);
        });
    }

    [Test]
    public async Task ARememberedRunnerThatWasOnlyCreated_IsRemoved_Unreported()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"starting {launchId} {target} -");
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal));
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(processes.Calls.Select(call => string.Join(' ', call.Arguments)), Does.Contain("rm abc"));
            Assert.That(reports, Is.Zero, "It was only created: nothing ran.");
        });
    }

    [Test]
    public async Task ALaunchedRunnerWhoseReportOffloadrDidNotTake_StaysNotedAsGone()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target -");
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"));

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) => Task.FromResult(false));

        Assert.That(File.ReadAllText(_paths.LaunchedContainers).Trim(), Is.EqualTo($"gone {launchId} target -"),
            "A later start of the app reports it.");
    }

    [Test]
    public async Task ARememberedRunnerWhoseStartWasAsked_IsNotReportedGone_WhileItIsStillCreatedAndItsRemovalFails()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string target = "https://offloadr.example#runner";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} -");
        Remember("abc");

        // Found created, the engine will not remove it, and it is still only created: it may start it yet.
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: target).Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal))
            .On("rm", 1, stderr: "Error response from daemon: driver failed to remove the container");
        var reports = 0;

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            });
        }
        catch (ContainerEngineException)
        {
            // The removal failed: told to the caller.
        }

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Zero, "Told gone, the allocation would launch again beside a container that may come to run.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers).Trim(), Does.StartWith($"running {launchId} "), "Its record stays as it is.");
        });
    }

    [Test]
    public async Task AContainerFoundByItsRecord_WhoseStartWasAsked_IsNotReportedGone_WhileItIsStillCreatedAndItsRemovalFails()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        var record = $"running {launchId} target {ContainerEngineSelection.Describe(engine)}";
        File.WriteAllText(_paths.LaunchedContainers, record);
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: "target").Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal))
            .On("rm", 1, stderr: "Error response from daemon: driver failed to remove the container");
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Zero, "Still only created, it may be started yet: nothing is told of it.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers).Trim(), Is.EqualTo(record), "Its record stays as it is.");
        });
    }

    [Test]
    public void ALaunchedRunnerThatStoppedButWasNotRemoved_IsStillReportedGone()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target -");
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"))
            .On("rm", 1, stderr: "device or resource busy");
        var reports = 0;

        Assert.ThrowsAsync<ContainerEngineException>(() => RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        }));

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.EqualTo(1), "It stopped: it serves nothing, and the app may never run again to say so.");
            Assert.That(File.Exists(_paths.EngineState), Is.True, "Still looked for on that engine.");
        });
    }

    [Test]
    public async Task AContainerNotedGoneEarlier_IsReported_ThoughNoContainerIsLeft()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"gone {launchId} target - instance-1");
        var reported = new List<RunnerCleanup.StoppedLaunch>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, new FakeProcessRunner(), new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { new RunnerCleanup.StoppedLaunch(launchId, "target") }),
                "Stopped while Offloadr could not be reached: no later start of the app may tell it.");
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False);
        });
    }

    [Test]
    public async Task AContainerStillNotedAsRunning_ThatIsGoneFromItsEngine_IsReported()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");

        // Removed while the app was closed: its engine answers, and has no container of that launch.
        var processes = new FakeProcessRunner().On("ps", 0, string.Empty);
        var reported = new List<RunnerCleanup.StoppedLaunch>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { new RunnerCleanup.StoppedLaunch(launchId, "target") }));
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False);
        });
    }

    [Test]
    public async Task AContainerStillNotedAsRunning_UnderAnotherName_IsStoppedRemovedAndReported()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"));
        var reports = 0;

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True, "It held the runner key.");
            Assert.That(commands, Does.Contain("stop --time 3 abc"), "A short grace, so its removal and its report still fit.");
            Assert.That(commands, Does.Contain("rm --force abc"));
            Assert.That(reports, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task WhatWasSettledOnOneEngine_IsSaved_BeforeAnotherUsesUpTheTime()
    {
        const string first = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string second = "9f0e6c0a-52c1-4f0e-9a43-0a3a5b7d1c11";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllLines(_paths.LaunchedContainers,
        [
            $"running {first} target {ContainerEngineSelection.Describe(engine)}",
            $"running {second} target {ContainerEngineSelection.Describe(engine)}",
        ]);

        // The first is gone from its engine; the look for the second runs out of time.
        using var time = new CancellationTokenSource();
        var looks = 0;
        var processes = new FakeProcessRunner().On("ps", 0, string.Empty);
        processes.OnRun = (spec, token) =>
        {
            if (spec.Arguments[0] == "ps" && ++looks == 2)
            {
                time.Cancel();
            }

            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), time.Token, (_, _) => Task.FromResult(false));
        }
        catch (OperationCanceledException)
        {
            // Out of time.
        }

        var noted = File.ReadAllLines(_paths.LaunchedContainers);
        Assert.Multiple(() =>
        {
            Assert.That(noted, Has.Length.EqualTo(2));
            Assert.That(noted[0], Does.StartWith($"gone {first} "), "Settled, and saved: the report that follows, now or at a later start, tells Offloadr.");
            Assert.That(noted[1], Does.StartWith($"running {second} "), "Not looked at: as it was.");
        });
    }

    [Test]
    public async Task AContainerItStopped_IsNotedGone_BeforeItsRemovalUsesUpTheTime()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");

        // It stops, and then its removal runs out of time.
        using var time = new CancellationTokenSource();
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"));
        processes.OnRun = (spec, token) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                time.Cancel();
            }

            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        try
        {
            await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), time.Token, (_, _) => Task.FromResult(false));
        }
        catch (OperationCanceledException)
        {
            // Out of time.
        }

        Assert.Multiple(() =>
        {
            Assert.That(processes.Calls.Select(call => call.Arguments[0]), Does.Contain("stop"));
            Assert.That(File.ReadAllText(_paths.LaunchedContainers), Does.StartWith($"gone {launchId} "),
                "It stopped without a grant: Offloadr is still to be told, whatever became of its removal.");
            Assert.That(File.ReadAllText(_paths.LeftoverContainers).Trim(), Is.EqualTo($"{ContainerEngineSelection.Describe(engine)} abc"),
                "It still holds the runner key there: removed at a later start of the app.");
        });
    }

    [Test]
    public async Task AContainerItStopped_IsNotedForRemoval_BeforeItsRecordIsSettled_AndTheNoteGoesWithIt()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"));

        // As its removal begins (where the installer may be cut short): what is on disk then.
        string? recordAtRemoval = null;
        string? leftoversAtRemoval = null;
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                recordAtRemoval = File.ReadAllText(_paths.LaunchedContainers);
                leftoversAtRemoval = File.Exists(_paths.LeftoverContainers) ? File.ReadAllText(_paths.LeftoverContainers).Trim() : null;
            }

            return Task.CompletedTask;
        };

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) => Task.FromResult(true));

        Assert.Multiple(() =>
        {
            Assert.That(recordAtRemoval, Does.StartWith($"gone {launchId} "));
            Assert.That(leftoversAtRemoval, Is.EqualTo($"{ContainerEngineSelection.Describe(engine)} abc"),
                "With its record settled, only this note would have a later start of the app remove it: it still holds the runner key.");
            Assert.That(File.Exists(_paths.LeftoverContainers), Is.False, "Removed: nothing is left to remove later.");
        });
    }

    [Test]
    public async Task AContainerStartedForAnEarlierAddressAndKey_IsReportedAsTheRunnerItWasStartedFor()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string earlierKey = "an-earlier-runner-key-0123456789abcdefghijklm";
        var earlier = new RunnerSettings { OffloadrApiOrigin = "https://earlier.offloadr.example", RunnerKey = earlierKey };
        var current = new RunnerSettings { RunnerKey = "the-current-runner-key-0123456789abcdefghijk" };
        var target = RunnerSupervisor.LaunchTargetFor(earlier);
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} {ContainerEngineSelection.Describe(engine)}");

        // Still running with the address and key it was started with; Settings name another runner by now.
        var inspect = Inspect(OwnerId, launchId: launchId, target: target)
            .Replace("\"Config\":{", $"\"Config\":{{\"Env\":[\"OFFLOADR_API_GRPC={earlier.OffloadrApiOrigin}\",\"RUNNER_SECRET={earlierKey}\"],", StringComparison.Ordinal);
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, inspect);
        var reported = new List<RunnerCleanup.StoppedLaunch>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        });

        var stopped = reported.Single();
        var asked = RunnerCleanup.ReportAs(stopped, current);
        Assert.Multiple(() =>
        {
            Assert.That(stopped.Target, Is.EqualTo(target));
            Assert.That(asked.OffloadrApiOrigin, Is.EqualTo(earlier.OffloadrApiOrigin), "Only the Offloadr that launched it can be told.");
            Assert.That(asked.RunnerKey, Is.EqualTo(earlierKey));
            Assert.That(stopped.ToString(), Does.Not.Contain(earlierKey), "The key is never printed.");
            Assert.That(RunnerCleanup.ReportAs(stopped with { Target = RunnerSupervisor.LaunchTargetFor(current) }, current), Is.SameAs(current),
                "Settings that name its runner are used as they are.");
            Assert.That(RunnerCleanup.ReportAs(stopped with { Target = "another-target" }, current), Is.SameAs(current),
                "What it was started with is used only when it names the runner it was launched for.");
        });
    }

    [Test]
    public async Task ReadingWhatAContainerWasStartedWith_NeverCostsItsStop()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");

        // The engine stops answering inspections right after the container was found.
        var inspections = 0;
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"));
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments[0] == "container" && Interlocked.Increment(ref inspections) > 1)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reported = new List<RunnerCleanup.StoppedLaunch>();
        using var installer = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), installer.Token, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        });

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(commands, Does.Contain("rm --force abc"), "Stopped and removed all the same: it holds the runner key.");
            Assert.That(reported.Single(), Is.EqualTo(new RunnerCleanup.StoppedLaunch(launchId, "target")), "Reported with the settings in the data folder.");
        });
    }

    [Test]
    public async Task AContainerThatIsGone_IsReportedAsTheRunnerItWasStartedFor_FromWhatTheAppSaved()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string earlierKey = "an-earlier-runner-key-0123456789abcdefghijklm";
        var earlier = new RunnerSettings { OffloadrApiOrigin = "https://earlier.offloadr.example", RunnerKey = earlierKey };
        var current = new RunnerSettings { RunnerKey = "the-current-runner-key-0123456789abcdefghijk" };
        var target = RunnerSupervisor.LaunchTargetFor(earlier);
        var protector = new ReversingSecretProtector();
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} {target} {ContainerEngineSelection.Describe(engine)}");
        File.WriteAllText(_paths.LaunchedCredentials, LaunchedCredentialsFile.Format(
            [new KeyValuePair<string, RunnerCleanup.LaunchCredentials>(launchId, new RunnerCleanup.LaunchCredentials(earlier.OffloadrApiOrigin, earlierKey))], protector));

        Assert.That(File.ReadAllText(_paths.LaunchedCredentials), Does.Not.Contain(earlierKey), "Saved encrypted.");

        // Gone from its engine: nothing is left there to read what it was started with.
        var processes = new FakeProcessRunner().On("ps", 0, string.Empty);
        var reported = new List<RunnerCleanup.StoppedLaunch>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        }, protector: protector);

        var asked = RunnerCleanup.ReportAs(reported.Single(), current);
        Assert.Multiple(() =>
        {
            Assert.That(asked.OffloadrApiOrigin, Is.EqualTo(earlier.OffloadrApiOrigin), "Only the Offloadr that launched it can be told.");
            Assert.That(asked.RunnerKey, Is.EqualTo(earlierKey));
            Assert.That(File.Exists(_paths.LaunchedCredentials), Is.False,
                "Offloadr took the report: no report needs that key any more, and the data folder outlives the app.");
        });
    }

    [Test]
    public async Task CredentialsOfALaunchStillNoted_AreKept_WhenOthersAreDropped()
    {
        const string settled = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string unresolved = "9f0e6c0a-52c1-4f0e-9a43-0a3a5b7d1c11";
        var protector = new ReversingSecretProtector();
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllText(_paths.LaunchedContainers, $"gone {settled} target -\ngone {unresolved} target -");
        File.WriteAllText(_paths.LaunchedCredentials, LaunchedCredentialsFile.Format(
        [
            new KeyValuePair<string, RunnerCleanup.LaunchCredentials>(settled, new RunnerCleanup.LaunchCredentials("https://a.example", "key-a-0123456789")),
            new KeyValuePair<string, RunnerCleanup.LaunchCredentials>(unresolved, new RunnerCleanup.LaunchCredentials("https://b.example", "key-b-0123456789")),
        ], protector));

        // Offloadr takes the report of the first, and cannot be reached for the second.
        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, new FakeProcessRunner(), new SupervisorOptions(), CancellationToken.None,
            (stopped, _) => Task.FromResult(stopped.LaunchId == settled), protector: protector);

        var kept = LaunchedCredentialsFile.Read(File.ReadAllText(_paths.LaunchedCredentials), protector);
        Assert.That(kept.Keys, Is.EqualTo(new[] { unresolved }), "Kept only for the report that is still owed.");
    }

    [Test]
    public async Task EnginesThatHang_DoNotCostTheRunnerOnAHealthyOneItsStop()
    {
        const string healthy = "9f0e6c0a-52c1-4f0e-9a43-0a3a5b7d1c11";
        string[] hung = ["3b101cf7-f2a4-4dab-82d9-c3d084428025", "3b101cf7-f2a4-4dab-82d9-c3d084428026", "3b101cf7-f2a4-4dab-82d9-c3d084428027"];
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = ContainerEngineSelection.Describe(new ContainerEngineSelection.Selected(null, Environment.ProcessPath!));
        File.WriteAllLines(_paths.LaunchedContainers, [.. hung.Append(healthy).Select(launch => $"running {launch} target {engine}")]);
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: true, launchId: healthy, target: "target"));
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments[0] == "ps" && !spec.Arguments.Any(argument => argument.Contains(healthy, StringComparison.Ordinal)))
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reported = new List<string>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped.LaunchId);
            return Task.FromResult(true);
        });

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(commands, Has.Some.StartsWith("stop "));
            Assert.That(commands, Does.Contain("rm --force abc"));
            Assert.That(reported, Is.EqualTo(new[] { healthy }));
            Assert.That(File.ReadAllLines(_paths.LaunchedContainers).Select(line => line.Split(' ')[1]), Is.EqualTo(hung), "Not known: as they were.");
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AContainerGoneAfterTheAnswerToItsRemovalWasLost_IsRemoved(bool remembered)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");
        if (remembered)
        {
            Remember("abc");
        }

        // The removal is done, and its answer (with the look right after it) lost.
        var looks = 0;
        var removing = false;
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: "target"))
            .On("rm", 1, stderr: "error during connect: EOF");
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "rm" && !removing)
            {
                removing = true;
                processes.On("container", 1, stderr: "error during connect: EOF").On("ps", 0, string.Empty);
            }
            else if (spec.Arguments[0] == "container" && removing && ++looks == 2)
            {
                processes.On("container", 1, stderr: "Error: No such container: abc");
            }

            return Task.CompletedTask;
        };
        var reported = new List<string>();

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped.LaunchId);
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(reported, Is.EqualTo(new[] { launchId }));
            Assert.That(File.Exists(_paths.EngineState), Is.False, "Nothing is left to look for there.");
            Assert.That(File.Exists(_paths.LeftoverContainers), Is.False, "Nor to remove.");
        });
    }

    [Test]
    public async Task AnEngineThatHangs_DoesNotCostTheNextOneItsTurn()
    {
        const string hung = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string gone = "9f0e6c0a-52c1-4f0e-9a43-0a3a5b7d1c11";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllLines(_paths.LaunchedContainers,
        [
            $"running {hung} target {ContainerEngineSelection.Describe(engine)}",
            $"running {gone} target {ContainerEngineSelection.Describe(engine)}",
        ]);

        // The look for the first never returns; the second is gone from its engine.
        var looks = 0;
        var processes = new FakeProcessRunner().On("ps", 0, string.Empty);
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments[0] == "ps" && Interlocked.Increment(ref looks) == 1)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reported = new List<string>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped.LaunchId);
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { gone }), "Looked for, and told, though the engine before it hung.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers).Trim(), Does.StartWith($"running {hung} "), "Not known: as it was.");
        });
    }

    [Test]
    public async Task ARemovalThatHangs_DoesNotCostTheNextRecordItsTurn()
    {
        const string hung = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string next = "9f0e6c0a-52c1-4f0e-9a43-0a3a5b7d1c11";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllLines(_paths.LaunchedContainers,
        [
            $"running {hung} target {ContainerEngineSelection.Describe(engine)}",
            $"running {next} target {ContainerEngineSelection.Describe(engine)}",
        ]);

        // The first is found stopped, and its removal never returns; the second is gone from its engine.
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: hung, target: "target"));
        processes.Answer = spec => spec.Arguments[0] == "ps" && !spec.Arguments.Any(argument => argument.Contains(hung, StringComparison.Ordinal))
            ? new Offloadr.Runner.Supervisor.Processes.ProcessResult(0, string.Empty, string.Empty)
            : null;
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments[0] == "rm")
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reported = new List<string>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            lock (reported)
            {
                reported.Add(stopped.LaunchId);
            }

            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EquivalentTo(new[] { hung, next }), "Both serve nothing any more, and are told.");
            Assert.That(File.ReadAllText(_paths.LeftoverContainers), Does.Contain(" abc"), "Not removed: noted, so a later start of the app removes it.");
        });
    }

    [Test]
    public async Task ALookForAContainerThatMayStillBeMade_ThatHangs_DoesNotCostTheNextRecordItsTurn()
    {
        const string pending = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string gone = "9f0e6c0a-52c1-4f0e-9a43-0a3a5b7d1c11";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllLines(_paths.LaunchedContainers,
        [
            $"starting {pending} target {ContainerEngineSelection.Describe(engine)}",
            $"running {gone} target {ContainerEngineSelection.Describe(engine)}",
        ]);

        // The first look for the container still being made finds nothing; the next one never returns.
        var looks = 0;
        var processes = new FakeProcessRunner().On("ps", 0, string.Empty);
        processes.OnRun = async (spec, token) =>
        {
            if (spec.Arguments[0] == "ps" && spec.Arguments.Any(argument => argument.Contains(pending, StringComparison.Ordinal)) && Interlocked.Increment(ref looks) == 2)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var reported = new List<string>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions { CancelledLaunchSettle = TimeSpan.FromSeconds(1) }, CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped.LaunchId);
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { gone }), "Looked for, and told, though the look before it hung.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers).Trim(), Does.StartWith($"starting {pending} "), "Not known: as it was.");
        });
    }

    [Test]
    public async Task AReportThatHangs_DoesNotCostTheOthersTheirs()
    {
        const string slow = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        const string quick = "9f0e6c0a-52c1-4f0e-9a43-0a3a5b7d1c11";
        File.WriteAllText(_paths.Owner, OwnerId);
        File.WriteAllLines(_paths.LaunchedContainers, [$"gone {slow} https://earlier.example#runner -", $"gone {quick} https://offloadr.example#runner -"]);
        var reported = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // The Offloadr of the first never answers.
        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, new FakeProcessRunner(), new SupervisorOptions(), CancellationToken.None, async (stopped, token) =>
        {
            if (stopped.LaunchId == slow)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            reported.Enqueue(stopped.LaunchId);
            return true;
        });

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.EqualTo(new[] { quick }), "Told on its own, whatever the other Offloadr does.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers).Trim(), Does.StartWith($"gone {slow} ").And.Not.Contain(quick),
                "What was taken is saved; what was not stays noted.");
        });
    }

    [Test]
    public async Task AContainerWhoseStartWasAsked_IsReportedGone_ThoughItIsFoundCreated()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: "target").Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal));
        var reported = new List<RunnerCleanup.StoppedLaunch>();

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (stopped, _) =>
        {
            reported.Add(stopped);
            return Task.FromResult(true);
        });

        Assert.That(reported, Is.EqualTo(new[] { new RunnerCleanup.StoppedLaunch(launchId, "target") }),
            "Its start was asked for: it may have run, whatever it looks like now.");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AContainerWhoseStartWasRefused_IsReportedGone_OnlyWhenItIsNotThereToShowItNeverRan(bool there)
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"refused {launchId} target {ContainerEngineSelection.Describe(engine)}");
        var processes = new FakeProcessRunner()
            .On("ps", 0, there ? "abc" : string.Empty)
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: "target").Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal));
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.EqualTo(there ? 0 : 1));
            Assert.That(processes.Calls.Any(call => call.Arguments[0] == "rm"), Is.EqualTo(there));
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False, "Settled either way.");
        });
    }

    [Test]
    public async Task ALaunchNotedAsStarting_WhoseContainerDoesNotShow_StaysNoted_Unreported()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        var record = $"starting 3b101cf7-f2a4-4dab-82d9-c3d084428025 target {ContainerEngineSelection.Describe(engine)}";
        File.WriteAllText(_paths.LaunchedContainers, record);
        var processes = new FakeProcessRunner().On("ps", 0, string.Empty);
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions { CancelledLaunchSettle = TimeSpan.FromMilliseconds(200) }, CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.Zero, "Nothing ran.");
            Assert.That(processes.Calls.Count(call => call.Arguments[0] == "ps"), Is.GreaterThan(1), "Looked for a moment longer: its creation may still be under way.");
            Assert.That(File.ReadAllText(_paths.LaunchedContainers).Trim(), Is.EqualTo(record),
                "Kept: the engine may still make it, and a later start of the app looks for it (it would hold the runner key).");
        });
    }

    [Test]
    public async Task ALaunchNotedAsStarting_WhoseContainerShowsAMomentLater_IsRemoved_Unreported()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"starting {launchId} target {ContainerEngineSelection.Describe(engine)}");

        // Not there at the first look; the engine has made it by the second.
        var processes = new FakeProcessRunner()
            .On("ps", 0, string.Empty)
            .On("container", 0, Inspect(OwnerId, running: false, launchId: launchId, target: "target").Replace("\"State\":{", "\"State\":{\"Status\":\"created\",", StringComparison.Ordinal));
        var looks = 0;
        processes.OnRun = (spec, _) =>
        {
            if (spec.Arguments[0] == "ps" && ++looks == 2)
            {
                processes.On("ps", 0, "abc");
            }

            return Task.CompletedTask;
        };
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions { CancelledLaunchSettle = TimeSpan.FromSeconds(2) }, CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(processes.Calls.Select(call => string.Join(' ', call.Arguments)), Does.Contain("rm abc"), "It holds the runner key.");
            Assert.That(reports, Is.Zero, "It was only created: nothing ran.");
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False);
        });
    }

    [Test]
    public async Task ARunningContainerFoundByItsRecord_IsReleasedFirst_AndNotReportedWhenGranted()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"));
        var order = new List<string>();
        processes.OnRun = (spec, _) =>
        {
            order.Add(spec.Arguments[0]);
            return Task.CompletedTask;
        };
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None,
            (_, _) =>
            {
                reports++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                order.Add("release");
                return Task.FromResult(true);
            });

        Assert.Multiple(() =>
        {
            Assert.That(order.IndexOf("release"), Is.LessThan(order.IndexOf("stop")), "Nothing is assigned to it while it stops.");
            Assert.That(reports, Is.Zero, "Offloadr released it: it knows.");
            Assert.That(File.Exists(_paths.LaunchedContainers), Is.False);
        });
    }

    [Test]
    public async Task AContainerFoundByItsRecord_ThatStoppedButWasNotRemoved_IsStillReportedGone()
    {
        const string launchId = "3b101cf7-f2a4-4dab-82d9-c3d084428025";
        File.WriteAllText(_paths.Owner, OwnerId);
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LaunchedContainers, $"running {launchId} target {ContainerEngineSelection.Describe(engine)}");
        var processes = new FakeProcessRunner()
            .On("ps", 0, "abc")
            .On("container", 0, Inspect(OwnerId, launchId: launchId, target: "target"))
            .On("rm", 1, stderr: "device or resource busy");
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.Multiple(() =>
        {
            Assert.That(reports, Is.EqualTo(1), "It stopped: it serves nothing, whatever became of its removal.");
            Assert.That(File.ReadAllText(_paths.LeftoverContainers).Trim(), Is.EqualTo($"{ContainerEngineSelection.Describe(engine)} abc"),
                "It still holds the runner key on that engine: noted, so a later start of the app removes it.");
        });
    }

    [Test]
    public async Task ARunnerOffloadrAlreadyKnowsIsGone_IsNotReported()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, launchId: "3b101cf7-f2a4-4dab-82d9-c3d084428025", target: "target"));
        var reports = 0;

        await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None, (_, _) =>
        {
            reports++;
            return Task.FromResult(true);
        });

        Assert.That(reports, Is.Zero, "No note of it: Offloadr released it, or was told.");
    }

    [Test]
    public void ARememberedEngineThatFails_StillLetsTheStoppedLeftoversBeRemoved()
    {
        File.WriteAllText(_paths.Owner, OwnerId);
        Remember("remembered");
        var engine = new ContainerEngineSelection.Selected(null, Environment.ProcessPath!);
        File.WriteAllText(_paths.LeftoverContainers, $"{ContainerEngineSelection.Describe(engine)} leftover");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId, running: false))
            .On("rm", 0);
        processes.OnRun = (spec, _) => spec.Arguments.Contains("remembered")
            ? throw new Offloadr.Runner.Supervisor.Processes.ProcessStartException("docker", new InvalidOperationException("The engine is gone."))
            : Task.CompletedTask;

        Assert.ThrowsAsync<ContainerEngineException>(
            () => RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None),
            "The failure is still the caller's to log.");

        var commands = processes.Calls.Select(call => string.Join(' ', call.Arguments)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(commands, Does.Contain("rm leftover"), "It holds the runner key, on an engine that answers.");
            Assert.That(File.Exists(_paths.LeftoverContainers), Is.False);
        });
    }

    [Test]
    public async Task WithoutAnOwnerFile_NothingIsRemoved()
    {
        Remember("abc");
        var processes = new FakeProcessRunner()
            .On("container", 0, Inspect(OwnerId));

        var removed = await RunnerCleanup.RemoveLeftoverRunnerAsync(_paths, processes, new SupervisorOptions(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(File.Exists(_paths.Owner), Is.False, "Cleanup must not create an owner ID.");
        });
    }

    private void Remember(string containerId)
    {
        // An executable that exists everywhere, so cleanup finds the remembered engine on any machine;
        // the fake process runner answers its commands.
        var docker = Environment.ProcessPath!;
        var selection = new ContainerEngineSelection(_ => new LocatedEngine(docker, ContainerEngineKind.Docker), _paths.EngineState);
        selection.Select(null);
        Assert.That(selection.Remember(containerId, out var error), Is.True, error);
    }

    private static string Inspect(string owner, bool running = true, string? launchId = null, string? target = null)
    {
        var launch = launchId is null
            ? string.Empty
            : $$$""","app.offloadr.runner.launch-id":"{{{launchId}}}","app.offloadr.runner.launch-target":"{{{target}}}" """.TrimEnd();
        return $$$"""[{"Id":"abc","Image":"sha256:feed","Config":{"Image":"ghcr.io/offloadr/runner-comfyui:latest","Labels":{"app.offloadr.runner.managed":"true","app.offloadr.runner.owner":"{{{owner}}}"{{{launch}}}}},"State":{"Running":{{{(running ? "true" : "false")}}}}}]""";
    }
}
