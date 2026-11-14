using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests.Linux;

public class ModelLibraryTests
{
    private string _root = null!;
    private string _library = null!;
    private string _editorModels = null!;

    [SetUp]
    public void SetUp()
    {
        LinuxTestPrerequisites.RequireLinux();
        _root = Path.Combine(Path.GetTempPath(), $"model-library-{Guid.NewGuid():N}");
        _library = Path.Combine(_root, "library");
        _editorModels = Path.Combine(_root, "comfyui", "models");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_editorModels);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private const string Session = "session-1";

    // The read-only view is the same folder here; in the runner it is a second, read-only mount of it.
    private ModelLibrary CreateLibrary(string runnerId = "11111111-1111-4111-8111-111111111111") => new(_library, _library, _editorModels, runnerId);

    [Test]
    public void Redirect_SendsADownloadToTheFolderAndLinksTheEditorsPathThere()
    {
        var request = Remote("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([request], Session).Single();

        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
            Assert.That(
                new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")).LinkTarget,
                Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
            Assert.That(request.DestinationPath, Is.EqualTo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")), "The request itself is left alone.");
        });
    }

    [Test]
    public void Redirect_IsRepeatable()
    {
        var library = CreateLibrary();
        var request = Remote("loras/style.safetensors", 8);

        var first = library.Redirect([request], Session).Single();
        var second = library.Redirect([request], Session).Single();

        Assert.That(second.DestinationPath, Is.EqualTo(first.DestinationPath));
    }

    [Test]
    public async Task Redirect_FromPromptsAtOnce_SendsThemAllToTheSameDownload()
    {
        var library = CreateLibrary();
        var request = Remote("checkpoints/base.safetensors", 8);

        var prompts = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => library.Redirect([request], Session).Single())));

        Assert.That(
            prompts.Select(static redirected => redirected.DestinationPath),
            Has.All.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")),
            "None falls back to downloading over another's link.");
    }

    [Test]
    public void Redirect_CountsAFileOfTheRequestedSizeAsTheModel()
    {
        WriteLibraryFile("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([Remote("checkpoints/base.safetensors", 8)], Session).Single();

        Assert.That(redirected.DestinationPath, Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
    }

    [Test]
    public void Redirect_LeavesTheOwnersFileOfAnotherSizeAndDownloadsInsideTheContainer()
    {
        WriteLibraryFile("checkpoints/base.safetensors", 16);
        var request = Remote("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([request], Session).Single();

        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(request.DestinationPath));
            Assert.That(new FileInfo(Path.Combine(_library, "checkpoints", "base.safetensors")).Length, Is.EqualTo(16));
            Assert.That(File.Exists(Path.Combine(_editorModels, "checkpoints", "base.safetensors")), Is.False);
        });
    }

    [Test]
    public void Redirect_LeavesAModelAlreadyAtTheEditorsPath()
    {
        var editorPath = Path.Combine(_editorModels, "checkpoints", "base.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(editorPath)!);
        File.WriteAllBytes(editorPath, new byte[8]);
        var request = Remote("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([request], Session).Single();

        Assert.That(redirected.DestinationPath, Is.EqualTo(request.DestinationPath));
    }

    [Test]
    public void Redirect_LeavesLocalRequestsUnknownSizesAndOtherPaths()
    {
        var local = Remote("checkpoints/mine.safetensors", 8);
        local.LocalSelectionHash = new string('a', 64);
        var unknownSize = Remote("checkpoints/unknown.safetensors", 0);
        var elsewhere = new ModelDownloadRequest
        {
            ModelId = "model-9",
            Filename = "other.safetensors",
            DestinationPath = Path.Combine(_root, "elsewhere", "other.safetensors"),
            SizeBytes = 8,
        };

        var redirected = CreateLibrary().Redirect([local, unknownSize, elsewhere], Session);

        Assert.That(redirected.Select(static request => request.DestinationPath), Is.EqualTo(new[]
        {
            local.DestinationPath,
            unknownSize.DestinationPath,
            elsewhere.DestinationPath,
        }));
    }

    [Test]
    public void Publish_MovesTheFinishedFileIntoPlace()
    {
        var library = CreateLibrary();
        var destination = Path.Combine(_library, "loras", "style.safetensors");
        var staging = library.GetStagingPath(destination)!;
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllBytes(staging, new byte[8]);

        library.Publish(staging, destination);

        Assert.Multiple(() =>
        {
            Assert.That(staging, Does.Contain(ModelLibrary.StagingFolderName));
            Assert.That(new FileInfo(destination).Length, Is.EqualTo(8));
            Assert.That(Directory.Exists(Path.GetDirectoryName(staging)), Is.False, "Nothing is left in the download folder.");
        });
    }

    [Test]
    public void Publish_LeavesTheFileAndItsNewFoldersToTheFoldersOwner()
    {
        LinuxTestPrerequisites.RequireRoot();
        // The owner's folder on a Linux host, as aria2 leaves what it downloads.
        Chown("12345:12345", _library);
        var library = CreateLibrary();
        var destination = Path.Combine(_library, "loras", "sdxl", "style.safetensors");
        var staging = Stage(library, destination, 8);
        Chown("62000:62000", staging);

        library.Publish(staging, destination);

        Assert.Multiple(() =>
        {
            Assert.That(OwnerOf(destination), Is.EqualTo("12345:12345"), "The owner can delete and rename it.");
            Assert.That(OwnerOf(Path.Combine(_library, "loras")), Is.EqualTo("12345:12345"));
            Assert.That(OwnerOf(Path.Combine(_library, "loras", "sdxl")), Is.EqualTo("12345:12345"));
        });
    }

    [Test]
    public void PrepareStagingDirectory_LeavesTheDownloadFoldersToTheFoldersOwner()
    {
        LinuxTestPrerequisites.RequireRoot();
        Chown("12345:12345", _library);
        var library = CreateLibrary();
        var staging = library.GetStagingPath(Path.Combine(_library, "loras", "style.safetensors"))!;
        var key = Path.GetDirectoryName(staging)!;
        var runFolder = Path.GetDirectoryName(key)!;
        var runnerFolder = Path.GetDirectoryName(runFolder)!;

        library.PrepareStagingDirectory(staging);

        // A runner that never comes back leaves its part files there: the owner can remove them.
        Assert.Multiple(() =>
        {
            Assert.That(OwnerOf(Path.Combine(_library, ModelLibrary.StagingFolderName)), Is.EqualTo("12345:12345"));
            Assert.That(OwnerOf(runnerFolder), Is.EqualTo("12345:12345"));
            Assert.That(OwnerOf(runFolder), Is.EqualTo("12345:12345"));
            Assert.That(OwnerOf(key), Is.EqualTo("12345:12345"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AModelAlreadySaved_LeavesNoDownloadFolderBehind(bool downloadedFirst)
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: CreateLibrary());
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);
        var request = Remote("loras/style.safetensors", 4);
        if (downloadedFirst)
        {
            await await service.PreparePromptDownloadsAsync([request], CancellationToken.None, highPriority: true);
        }
        else
        {
            WriteLibraryFile("loras/style.safetensors", 4);
        }

        // Prompt after prompt, the model is already there.
        for (var prompt = 0; prompt < 3; prompt++)
        {
            service.RegisterDownloads(Session, [request]);
            await await service.PreparePromptDownloadsAsync([request], CancellationToken.None, highPriority: true);
        }

        var downloadFolder = Path.Combine(_library, ModelLibrary.StagingFolderName);
        Assert.That(
            Directory.Exists(downloadFolder) ? Directory.EnumerateDirectories(downloadFolder, "*", SearchOption.AllDirectories).Where(folder => Path.GetRelativePath(downloadFolder, folder).Split('/').Length > 2) : [],
            Is.Empty,
            "No download folder is left for a model already saved.");
        Assert.That(backend.Destinations, Has.Count.EqualTo(downloadedFirst ? 1 : 0));
    }

    [Test]
    public async Task ADownload_MakesItsDownloadFoldersTheFoldersOwners()
    {
        LinuxTestPrerequisites.RequireRoot();
        Chown("12345:12345", _library);
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        var library = CreateLibrary();
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: library);
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);

        await await service.PreparePromptDownloadsAsync([Remote("loras/style.safetensors", 4)], CancellationToken.None, highPriority: true);

        var downloadFolder = Path.Combine(_library, ModelLibrary.StagingFolderName);
        Assert.Multiple(() =>
        {
            Assert.That(OwnerOf(downloadFolder), Is.EqualTo("12345:12345"));
            Assert.That(OwnerOf(Directory.GetDirectories(downloadFolder).Single()), Is.EqualTo("12345:12345"));
        });
    }

    private static void Chown(string owner, string path)
    {
        using var chown = System.Diagnostics.Process.Start("/bin/chown", [owner, path])!;
        chown.WaitForExit();
        Assert.That(chown.ExitCode, Is.Zero);
    }

    private static string OwnerOf(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat", ["-c", "%u:%g", path]) { RedirectStandardOutput = true };
        using var stat = System.Diagnostics.Process.Start(start)!;
        var owner = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();
        return owner;
    }

    [Test]
    public void Publish_NeverReplacesAFilePutThereMeanwhile()
    {
        var library = CreateLibrary();
        var request = Remote("loras/style.safetensors", 8);
        library.Redirect([request], Session);
        // The owner copies in a file of the same name while it downloads.
        var destination = WriteLibraryFile("loras/style.safetensors", 4);
        var staging = library.GetStagingPath(destination)!;
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllBytes(staging, new byte[8]);

        Assert.That(() => library.Publish(staging, destination), Throws.InstanceOf<IOException>());
        var editorPath = new FileInfo(Path.Combine(_editorModels, "loras", "style.safetensors"));
        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(destination).Length, Is.EqualTo(4), "The owner's file is kept.");
            Assert.That(editorPath.Exists || editorPath.LinkTarget is not null, Is.False, "The editor's path no longer opens the owner's file.");
            Assert.That(
                library.Redirect([request], Session).Single().DestinationPath,
                Is.EqualTo(request.DestinationPath),
                "The session may still download the model inside the container.");
        });
    }

    [Test]
    public void Publish_OfADownloadTheOwnersFileTookThePlaceOf_LeavesTheLinkTheProjectorUsesForIt()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("loras/style.safetensors", 8)], Session);
        // The owner copies in a file of the same name while it downloads, and the session picks it as a
        // local model, through the same link.
        var destination = WriteLibraryFile("loras/style.safetensors", 4);
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("loras/style.safetensors", 4, ModelCategory.Lora)]);
        var staging = Stage(library, destination, 8);

        Assert.That(() => library.Publish(staging, destination), Throws.InstanceOf<IOException>());
        Assert.That(new FileInfo(Path.Combine(_editorModels, "loras", "style.safetensors")).LinkTarget, Is.EqualTo(destination));
    }

    [Test]
    public void Publish_OfADownloadTheOwnersFileTookThePlaceOf_LeavesALinkClaimedForTheOwnersFile()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("loras/style.safetensors", 8)], Session);
        // The owner copies in a file of the same name while it downloads, and the next session asks for a
        // model of just that size there, through the same link.
        var destination = WriteLibraryFile("loras/style.safetensors", 4);
        library.ReleaseSessionsOtherThan("session-2");
        library.Redirect([Remote("loras/style.safetensors", 4)], "session-2");
        var staging = Stage(library, destination, 8);

        Assert.That(() => library.Publish(staging, destination), Throws.InstanceOf<IOException>());
        var editorPath = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(editorPath).LinkTarget, Is.EqualTo(destination), "The next session's link stays.");
            Assert.That(library.KeepsEditorLink(editorPath), Is.True, "And so does its claim.");
        });
    }

    [Test]
    public void UndoingABatch_BringsBackNoClaimOfASessionThatHasEnded()
    {
        var library = CreateLibrary();
        library.ReleaseSessionsOtherThan(Session);
        var request = Remote("loras/style.safetensors", 4);
        library.Redirect([request], Session);
        // The next session's prompt redirects as that session becomes active, before the first one's claims are
        // released; then it is refused.
        library.Redirect([request], "session-2", out var refused);
        library.ReleaseSessionsOtherThan("session-2");

        library.Undo(refused);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.That(library.KeepsEditorLink(style), Is.False);
        var mine = WriteLibraryFile("loras/mine/style.safetensors", 6);
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("loras/style.safetensors", 6, ModelCategory.Lora)]);
        Assert.That(new FileInfo(style).LinkTarget, Is.EqualTo(mine));
    }

    [Test]
    public void DiscardAll_LeavesTheDownloadsOfAnOverlappingRunOfThisRunner_AndClearsAnEndedOnes()
    {
        var first = CreateLibrary();
        var firstStaging = Stage(first, Path.Combine(_library, "loras", "first.safetensors"), 3);
        // The runner is launched again while the first run still downloads.
        using var second = CreateLibrary();

        second.DiscardAll();
        Assert.That(File.Exists(firstStaging), Is.True, "A run still going keeps its downloads in progress.");

        first.Dispose();
        second.DiscardAll();

        var runFolder = Path.GetDirectoryName(Path.GetDirectoryName(firstStaging)!)!;
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(runFolder), Is.False, "An ended run's downloads in progress are cleared.");
            Assert.That(File.Exists(runFolder + ".lease"), Is.False);
        });
    }

    [Test]
    public async Task ASecondRunStarting_LeavesTheFirstRunsDownloadInProgress()
    {
        using var first = CreateLibrary();
        var firstStaging = Stage(first, Path.Combine(_library, "loras", "first.safetensors"), 3);
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        await using var second = new ModelDownloadService(
            new WritingBackend(),
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: CreateLibrary());

        await second.InitializeAsync(CancellationToken.None);

        Assert.That(File.Exists(firstStaging), Is.True);
    }

    [Test]
    public void Publish_WhereRenamesCannotRefuseATakenName_StillNeverReplacesIt()
    {
        LinuxSecureDirectoryRoot.RenameWithoutReplacingUnsupportedForTests = true;
        try
        {
            var library = CreateLibrary();
            var free = Path.Combine(_library, "loras", "new.safetensors");
            var freeStaging = Stage(library, free, 8);
            var taken = WriteLibraryFile("loras/taken.safetensors", 4);
            var takenStaging = Stage(library, taken, 8);

            library.Publish(freeStaging, free);

            Assert.Multiple(() =>
            {
                Assert.That(new FileInfo(free).Length, Is.EqualTo(8), "Moved by a hard link, then its old name removed.");
                Assert.That(File.Exists(freeStaging), Is.False);
                Assert.That(() => library.Publish(takenStaging, taken), Throws.InstanceOf<IOException>());
                Assert.That(new FileInfo(taken).Length, Is.EqualTo(4), "The owner's file is kept.");
            });
        }
        finally
        {
            LinuxSecureDirectoryRoot.RenameWithoutReplacingUnsupportedForTests = false;
        }
    }

    [Test]
    public void Publish_FindingTheSameModelAlreadyThere_CountsTheDownloadAsDone()
    {
        var library = CreateLibrary();
        var destination = Path.Combine(_library, "loras", "shared.safetensors");
        var staging = Stage(library, destination, 8);
        // Another runner sharing the folder saved the same model first.
        WriteLibraryFile("loras/shared.safetensors", 8);

        Assert.That(() => library.Publish(staging, destination), Throws.Nothing);
        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(destination).Length, Is.EqualTo(8));
            Assert.That(File.Exists(staging), Is.False, "The second copy is dropped.");
        });
    }

    [Test]
    public void DiscardAll_LeavesTheDownloadsOfAnotherRunnerSharingTheFolder()
    {
        var mine = CreateLibrary("11111111-1111-4111-8111-111111111111");
        var theirs = CreateLibrary("22222222-2222-4222-8222-222222222222");
        var theirStaging = Stage(theirs, Path.Combine(_library, "loras", "theirs.safetensors"), 3);
        var myStaging = Stage(mine, Path.Combine(_library, "loras", "mine.safetensors"), 3);

        mine.DiscardAll();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(myStaging), Is.False);
            Assert.That(File.Exists(theirStaging), Is.True, "Another runner's download in progress is left alone.");
        });
    }

    [Test]
    public void Redirect_OfAnotherSizeAtALinkedPath_InANewSession_RemovesTheLinkSoItDownloadsInTheContainer()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        WriteLibraryFile("checkpoints/base.safetensors", 8);
        var other = Remote("checkpoints/base.safetensors", 16);

        var redirected = library.Redirect([other], "session-2").Single();

        var editorPath = Path.Combine(_editorModels, "checkpoints", "base.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(editorPath));
            Assert.That(File.Exists(editorPath) || new FileInfo(editorPath).LinkTarget is not null, Is.False, "Its link to the saved model is gone.");
            Assert.That(new FileInfo(Path.Combine(_library, "checkpoints", "base.safetensors")).Length, Is.EqualTo(8), "The saved model stays.");
        });
    }

    [Test]
    public void Redirect_OfAnotherSizeAtALinkedPath_WithinASession_IsRefusedAndKeepsTheLink()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        WriteLibraryFile("checkpoints/base.safetensors", 8);

        // An earlier prompt may still be using the saved model.
        Assert.That(
            () => library.Redirect([Remote("checkpoints/base.safetensors", 16)], Session),
            Throws.InvalidOperationException.With.Message.Contains("already in use"));
        Assert.That(
            new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")).LinkTarget,
            Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
    }

    [Test]
    public void Redirect_OfAnotherSizeAtALinkedPath_IsRefusedToTheLatestSessionThatAskedForTheLinkedModel()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        WriteLibraryFile("checkpoints/base.safetensors", 8);

        // A later session asks for the same model, through the same link, and may be using it.
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], "session-2");

        Assert.That(
            () => library.Redirect([Remote("checkpoints/base.safetensors", 16)], "session-2"),
            Throws.InvalidOperationException.With.Message.Contains("already in use"));
        Assert.That(
            library.Redirect([Remote("checkpoints/base.safetensors", 16)], "session-3").Single().DestinationPath,
            Is.EqualTo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")));
    }

    [Test]
    public void ARedirectRacingTheProjectorsCleanupOfItsLink_KeepsALink()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        // Its session ended, so the projector may clean the link up.
        library.ReleaseSession(Session);
        Task<ModelDownloadRequest[]>? redirect = null;
        var links = new RacingLinks(
            library,
            () => redirect = Task.Run(() => library.Redirect([Remote("checkpoints/base.safetensors", 8)], "session-2")));
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: links);

        projector.SetRequestedModels([]);

        Assert.That(redirect, Is.Not.Null, "The redirect ran while the projector was removing the link.");
        Assert.That(redirect!.Wait(TimeSpan.FromSeconds(10)), Is.True);
        var saved = Path.Combine(_library, "checkpoints", "base.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(redirect.Result.Single().DestinationPath, Is.EqualTo(saved));
            Assert.That(new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")).LinkTarget, Is.EqualTo(saved));
        });
    }

    /// <summary>
    /// Starts a redirect at the moment the projector, about to change a link, has found it unclaimed, and gives
    /// it time to claim that link before the change is made.
    /// </summary>
    private sealed class RacingLinks(ModelLibrary library, Func<Task> race) : IDownloadLinks
    {
        private bool _changing;
        private Task? _racer;

        public bool KeepsEditorLink(string editorPath)
        {
            var kept = library.KeepsEditorLink(editorPath);
            if (_changing && !kept && _racer is null)
            {
                _racer = race();
                _racer.Wait(TimeSpan.FromMilliseconds(300));
            }

            return kept;
        }

        public void WhileLinksAreHeld(Action change) => library.WhileLinksAreHeld(() =>
        {
            _changing = true;
            try
            {
                change();
            }
            finally
            {
                _changing = false;
            }
        });

        public void SetProjected(string editorPath, bool projected) => library.SetProjected(editorPath, projected);
    }

    /// <summary>A request for the owner's model of that size, linked at the editor's path of that name.</summary>
    private ModelDownloadRequest LocalAt(string relative, long size, ModelCategory category) => new()
    {
        Filename = Path.GetFileName(relative),
        Category = category,
        SizeBytes = size,
        DestinationPath = Path.Combine(_editorModels, relative),
        LocalSelectionHash = LocalModelSelectionHash.Compute(Path.GetFileName(relative), size, category)
    };

    [Test]
    public void ADownloadOfAnotherSize_IsRefusedWhereTheProjectorLinksTheSavedModelForANewSession()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        var saved = WriteLibraryFile("checkpoints/base.safetensors", 8);
        library.ReleaseSessionsOtherThan("session-2");
        // The next session picks the saved file as a local model, through the same link.
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("checkpoints/base.safetensors", 8, ModelCategory.Checkpoint)]);

        Assert.That(
            () => library.Redirect([Remote("checkpoints/base.safetensors", 16)], "session-2"),
            Throws.InvalidOperationException.With.Message.Contains("already in use"));
        Assert.That(new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")).LinkTarget, Is.EqualTo(saved));

        // Once the local model is no longer asked for, the link gives way as before.
        projector.SetRequestedModels([]);
        Assert.That(
            library.Redirect([Remote("checkpoints/base.safetensors", 16)], "session-2").Single().DestinationPath,
            Is.EqualTo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ABatchRefusedAfterItIsRedirected_UndoesItsLinks(bool seeded)
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        var library = CreateLibrary();
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: library);
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);
        var outside = Remote("loras/escape.safetensors", 4);
        outside.DestinationPath = Path.Combine(_root, "elsewhere", "escape.safetensors");
        ModelDownloadRequest[] batch = [Remote("loras/style.safetensors", 4), outside];

        // Its second destination is outside the model roots, which is found only after the redirect.
        Assert.That(
            () =>
            {
                if (seeded)
                {
                    service.SeedDownloads(Session, batch);
                }
                else
                {
                    service.RegisterDownloads(Session, batch);
                }
            },
            Throws.InvalidOperationException);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(style) || new FileInfo(style).LinkTarget is not null, Is.False, "The first request's link is undone.");
            Assert.That(library.KeepsEditorLink(style), Is.False);
        });

        // The same session may have the owner's model linked there instead.
        var mine = WriteLibraryFile("loras/mine/style.safetensors", 6);
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("loras/style.safetensors", 6, ModelCategory.Lora)]);
        Assert.That(new FileInfo(style).LinkTarget, Is.EqualTo(mine));
    }

    // A batch is refused only after its redirect; meanwhile another, of the same session or another, sends its
    // download through the link the first one made.
    [TestCase(Session)]
    [TestCase("session-2")]
    public void UndoingABatch_LeavesALinkAnotherRedirectClaimedSince(string otherSession)
    {
        var library = CreateLibrary();
        var request = Remote("loras/style.safetensors", 4);
        library.Redirect([request], Session, out var refused);
        var other = library.Redirect([request], otherSession).Single();

        library.Undo(refused);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(other.DestinationPath, Is.EqualTo(Path.Combine(_library, "loras", "style.safetensors")));
            Assert.That(new FileInfo(style).LinkTarget, Is.EqualTo(other.DestinationPath), "The other download's link stays.");
            Assert.That(library.KeepsEditorLink(style), Is.True, "And so does its claim.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void APromptOfAnEndedSession_ClaimsNoLinkFromTheNextOne(bool cancelled)
    {
        var library = CreateLibrary();
        library.ReleaseSessionsOtherThan(Session);
        // The session ends, and the next one is active, while one of its prompts still waits to redirect.
        if (cancelled)
        {
            library.ReleaseSession(Session);
        }

        library.ReleaseSessionsOtherThan("session-2");

        library.Redirect([Remote("loras/style.safetensors", 4)], Session);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.That(library.KeepsEditorLink(style), Is.False);
        // The new session may have another local model linked there.
        var mine = WriteLibraryFile("loras/mine/style.safetensors", 6);
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("loras/style.safetensors", 6, ModelCategory.Lora)]);
        Assert.That(new FileInfo(style).LinkTarget, Is.EqualTo(mine));
    }

    [Test]
    public void APromptOfAnEndedSession_ChangesNoLinkOfTheNextOne()
    {
        var library = CreateLibrary();
        library.ReleaseSessionsOtherThan(Session);
        library.ReleaseSessionsOtherThan("session-2");
        // The next session links a saved model; then the ended one's prompt, which waited, asks for another
        // model of that name.
        var saved = WriteLibraryFile("checkpoints/base.safetensors", 8);
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], "session-2");
        var late = Remote("checkpoints/base.safetensors", 16);

        var redirected = library.Redirect([late], Session).Single();

        var editorPath = Path.Combine(_editorModels, "checkpoints", "base.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(late.DestinationPath));
            Assert.That(new FileInfo(editorPath).LinkTarget, Is.EqualTo(saved), "The next session's link stays.");
            Assert.That(library.KeepsEditorLink(editorPath), Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task APromptPreparedAfterItsSessionEnded_SendsNothingToTheFolder(bool cancelled)
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        var library = CreateLibrary();
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: library);
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);
        // The prompt's session is stopped, and maybe the next one active, before its downloads are prepared.
        if (cancelled)
        {
            service.CancelSession(Session);
        }
        else
        {
            service.SetActiveSession("session-2");
        }

        await await service.PreparePromptDownloadsAsync(
            [Remote("loras/style.safetensors", 4)],
            CancellationToken.None,
            highPriority: true,
            sessionId: Session);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(style).LinkTarget, Is.Null, "No link is made for it.");
            Assert.That(library.KeepsEditorLink(style), Is.False, "Nor claimed for the session now active.");
            Assert.That(File.Exists(Path.Combine(_library, "loras", "style.safetensors")), Is.False, "Nothing is saved in the folder.");
        });
    }

    [Test]
    public void ASessionActiveAgain_ClaimsItsLinksAgain()
    {
        var library = CreateLibrary();
        library.ReleaseSessionsOtherThan(Session);
        library.ReleaseSessionsOtherThan(null);
        library.ReleaseSessionsOtherThan(Session);

        library.Redirect([Remote("loras/style.safetensors", 4)], Session);

        Assert.That(library.KeepsEditorLink(Path.Combine(_editorModels, "loras", "style.safetensors")), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ADownloadFolderThatIsALink_IsNeverWrittenThrough(bool linkTheRunnersFolder)
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var downloadFolder = Path.Combine(_library, ModelLibrary.StagingFolderName);
        if (linkTheRunnersFolder)
        {
            Directory.CreateDirectory(downloadFolder);
            Directory.CreateSymbolicLink(Path.Combine(downloadFolder, "11111111-1111-4111-8111-111111111111"), elsewhere);
        }
        else
        {
            Directory.CreateSymbolicLink(downloadFolder, elsewhere);
        }

        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: CreateLibrary());
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);

        await await service.PreparePromptDownloadsAsync([Remote("loras/style.safetensors", 4)], CancellationToken.None, highPriority: true);

        var editorPath = new FileInfo(Path.Combine(_editorModels, "loras", "style.safetensors"));
        Assert.Multiple(() =>
        {
            Assert.That(editorPath.LinkTarget, Is.Null, "Downloaded inside the container instead.");
            Assert.That(editorPath.Length, Is.EqualTo(4));
            Assert.That(Directory.EnumerateFileSystemEntries(elsewhere), Is.Empty, "Nothing is written through the link.");
        });
    }

    [Test]
    public void CommittedBatches_AreNotKept()
    {
        var library = CreateLibrary();
        var request = Remote("loras/style.safetensors", 4);

        // The same model, prompt after prompt in one session.
        var earlier = RedirectAndCommit(library, request, times: 20);
        library.Redirect([request], Session, out var pending);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Multiple(() =>
        {
            Assert.That(earlier.Count(static batch => batch.TryGetTarget(out _)), Is.Zero, "No committed batch is kept.");
            Assert.That(pending.ClaimsBefore.Values.Single().By, Is.SameAs(ModelLibrary.RedirectBatch.Committed));
        });

        // An undo of the pending one still leaves the committed claim and its link.
        library.Undo(pending);
        Assert.That(library.KeepsEditorLink(Path.Combine(_editorModels, "loras", "style.safetensors")), Is.True);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<WeakReference<ModelLibrary.RedirectBatch>> RedirectAndCommit(ModelLibrary library, ModelDownloadRequest request, int times)
    {
        var batches = new List<WeakReference<ModelLibrary.RedirectBatch>>();
        for (var index = 0; index < times; index++)
        {
            library.Redirect([request], Session, out var batch);
            library.Commit(batch);
            batches.Add(new WeakReference<ModelLibrary.RedirectBatch>(batch));
        }

        return batches;
    }

    [Test]
    public void UndoingABatch_LeavesALinkTheProjectorAdoptedSince()
    {
        var library = CreateLibrary();
        var saved = WriteLibraryFile("checkpoints/base.safetensors", 8);
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session, out var refused);
        // Meanwhile the session picks the saved model as a local one, through the same link.
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("checkpoints/base.safetensors", 8, ModelCategory.Checkpoint)]);

        library.Undo(refused);

        Assert.That(new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")).LinkTarget, Is.EqualTo(saved));
    }

    [Test]
    public void UndoingTwoBatchesThroughOneLink_InEitherOrder_LeavesNoClaim([Values] bool makerFirst)
    {
        var library = CreateLibrary();
        var request = Remote("loras/style.safetensors", 4);
        library.Redirect([request], Session, out var maker);
        library.Redirect([request], "session-2", out var adopter);

        library.Undo(makerFirst ? maker : adopter);
        library.Undo(makerFirst ? adopter : maker);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(style) || new FileInfo(style).LinkTarget is not null, Is.False, "Neither batch keeps the link.");
            Assert.That(library.KeepsEditorLink(style), Is.False);
        });

        // Another local model can be linked there.
        var mine = WriteLibraryFile("loras/mine/style.safetensors", 6);
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("loras/style.safetensors", 6, ModelCategory.Lora)]);
        Assert.That(new FileInfo(style).LinkTarget, Is.EqualTo(mine));
    }

    [Test]
    public async Task APromptRefusedAfterItIsRedirected_UndoesItsLinks()
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        var library = CreateLibrary();
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: library);
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);
        var outside = Remote("loras/escape.safetensors", 4);
        outside.DestinationPath = Path.Combine(_root, "elsewhere", "escape.safetensors");

        Assert.That(
            async () => await service.PreparePromptDownloadsAsync(
                [Remote("loras/style.safetensors", 4), outside],
                CancellationToken.None,
                highPriority: true),
            Throws.InvalidOperationException);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.That(File.Exists(style) || new FileInfo(style).LinkTarget is not null, Is.False);
        Assert.That(library.KeepsEditorLink(style), Is.False);
    }

    [Test]
    public void ARefusedRequest_UndoesTheLinksItsBatchMade()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        WriteLibraryFile("checkpoints/base.safetensors", 8);

        Assert.That(
            () => library.Redirect([Remote("loras/style.safetensors", 4), Remote("checkpoints/base.safetensors", 16)], Session),
            Throws.InvalidOperationException);

        var style = Path.Combine(_editorModels, "loras", "style.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(style) || new FileInfo(style).LinkTarget is not null, Is.False, "The first request's link is undone.");
            Assert.That(library.KeepsEditorLink(style), Is.False);
            Assert.That(library.KeepsEditorLink(Path.Combine(_editorModels, "checkpoints", "base.safetensors")), Is.True, "Earlier links stay claimed.");
        });

        // The same session may have the owner's model linked there instead.
        var mine = WriteLibraryFile("loras/mine/style.safetensors", 6);
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.SetRequestedModels([LocalAt("loras/style.safetensors", 6, ModelCategory.Lora)]);
        Assert.That(new FileInfo(style).LinkTarget, Is.EqualTo(mine));
    }

    [Test]
    public void TheProjector_LeavesTheLinksDownloadsWentThrough_AndRemovesItsOwnStaleOnes()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        var downloadLink = Path.Combine(_editorModels, "checkpoints", "base.safetensors");
        // An earlier run's projection of another saved model, requested by nothing now.
        var other = WriteLibraryFile("loras/other.safetensors", 4);
        var staleProjection = Path.Combine(_editorModels, "loras", "other.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(staleProjection)!);
        File.CreateSymbolicLink(staleProjection, other);
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);

        // While the model downloads, and once it is saved and found by the scan.
        projector.SetRequestedModels([]);
        WriteLibraryFile("checkpoints/base.safetensors", 8);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        projector.AddRequestedModels([]);

        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(downloadLink).LinkTarget, Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
            Assert.That(new FileInfo(staleProjection).LinkTarget, Is.Null, "The projector still clears its own stale links.");
            Assert.That(File.Exists(other), Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OnceItsSessionEnds_ADownloadsLinkGivesWayToAnotherLocalModelAtThatPath(bool cancelled)
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        var library = CreateLibrary();
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: library);
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);
        await await service.PreparePromptDownloadsAsync(
            [Remote("checkpoints/base.safetensors", 8)],
            CancellationToken.None,
            highPriority: true);

        // The owner's own model of the same name, which a later session asks to have at that path.
        var mine = WriteLibraryFile("checkpoints/sdxl/base.safetensors", 12);
        var editorPath = Path.Combine(_editorModels, "checkpoints", "base.safetensors");
        var local = new ModelDownloadRequest
        {
            Filename = "base.safetensors",
            Category = ModelCategory.Checkpoint,
            SizeBytes = 12,
            DestinationPath = editorPath,
            LocalSelectionHash = LocalModelSelectionHash.Compute("base.safetensors", 12, ModelCategory.Checkpoint)
        };
        var projector = new LocalModelProjector(_library, _editorModels, downloadLinks: library);
        projector.UpdateSnapshot(new LocalModelScanner("runner-1", _library).Scan());
        Assert.That(() => projector.SetRequestedModels([local]), Throws.InvalidOperationException, "While its session lasts, the download's link stays.");

        if (cancelled)
        {
            service.CancelSession(Session);
        }
        else
        {
            service.SetActiveSession("session-2");
        }

        projector.SetRequestedModels([local]);

        Assert.That(new FileInfo(editorPath).LinkTarget, Is.EqualTo(mine));
    }

    [Test]
    public async Task APromptInANewSession_DownloadsAnotherModelAtAPathAnEarlierSessionLinked()
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: CreateLibrary());
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession(Session);
        var saved = Remote("checkpoints/base.safetensors", 8);
        await await service.PreparePromptDownloadsAsync([saved], CancellationToken.None, highPriority: true);

        // The next session seeds nothing, and its first prompt names another model at the same path.
        service.SetActiveSession("session-2");
        await await service.PreparePromptDownloadsAsync(
            [Remote("checkpoints/base.safetensors", 16)],
            CancellationToken.None,
            highPriority: true);

        var editorPath = new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors"));
        Assert.Multiple(() =>
        {
            Assert.That(editorPath.LinkTarget, Is.Null, "Downloaded inside the container, in place of the link.");
            Assert.That(editorPath.Length, Is.EqualTo(16));
            Assert.That(new FileInfo(Path.Combine(_library, "checkpoints", "base.safetensors")).Length, Is.EqualTo(8), "The saved model stays.");
        });
    }

    [Test]
    public void LibraryPathFor_FindsTheDownloadBehindTheEditorsLinkOnly()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)], Session);
        var other = Path.Combine(_editorModels, "loras", "own.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, new byte[2]);

        Assert.Multiple(() =>
        {
            Assert.That(
                library.LibraryPathFor(Path.Combine(_editorModels, "checkpoints", "base.safetensors")),
                Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
            Assert.That(library.LibraryPathFor(other), Is.Null);
        });
    }

    [Test]
    public async Task AnEditorOpeningItsLinkBeforeTheModelIsThere_WaitsForTheDownload()
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: CreateLibrary());
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession("session-1");
        service.SeedDownloads("session-1", [Remote("checkpoints/base.safetensors", 8)]);

        // What the shim asks for when the editor opens its own path, as at startup, without a prompt.
        var handled = await service.TryEnsureRegisteredDownloadAsync(
            Path.Combine(_editorModels, "checkpoints", "base.safetensors"),
            CancellationToken.None,
            highPriority: true);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(new FileInfo(Path.Combine(_library, "checkpoints", "base.safetensors")).Length, Is.EqualTo(8));
        });
    }

    private static string Stage(ModelLibrary library, string destination, int size)
    {
        var staging = library.GetStagingPath(destination)!;
        library.PrepareStagingDirectory(staging);
        File.WriteAllBytes(staging, new byte[size]);
        return staging;
    }

    [Test]
    public void Discard_NeverFollowsADownloadFolderSwappedForALink()
    {
        var library = CreateLibrary();
        var destination = Path.Combine(_library, "loras", "style.safetensors");
        var staging = library.GetStagingPath(destination)!;
        var downloadFolder = Path.Combine(_library, ModelLibrary.StagingFolderName);

        // Elsewhere, a tree laid out like this runner's downloads, and the download folder pointed at it.
        var outside = Path.Combine(_root, "outside");
        var outsideKey = Path.Combine(outside, Path.GetRelativePath(downloadFolder, Path.GetDirectoryName(staging)!));
        Directory.CreateDirectory(outsideKey);
        var sentinel = Path.Combine(outsideKey, "keep.txt");
        File.WriteAllText(sentinel, "not the runner's");
        Directory.CreateSymbolicLink(downloadFolder, outside);

        library.Discard(destination);

        Assert.That(File.Exists(sentinel), Is.True, "Nothing outside the models folder is deleted.");
    }

    [Test]
    public void Redirect_TreatsAFolderOfTheOwnersAtThePathAsTaken()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_library, "checkpoints", "base.safetensors")).FullName;
        var request = Remote("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([request], Session).Single();

        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(request.DestinationPath), "Downloaded inside the container instead.");
            Assert.That(Directory.Exists(folder), Is.True);
        });
    }

    [Test]
    public void Redirect_LeavesAModelWhoseFolderTheOwnerLinkedElsewhere()
    {
        // The owner keeps checkpoints on another drive.
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "other-drive", "checkpoints")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_library, "checkpoints"), elsewhere);
        var request = Remote("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([request], Session).Single();

        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(request.DestinationPath), "Downloaded inside the container.");
            Assert.That(new DirectoryInfo(Path.Combine(_library, "checkpoints")).LinkTarget, Is.EqualTo(elsewhere));
            Assert.That(Directory.EnumerateFileSystemEntries(elsewhere), Is.Empty);
            Assert.That(File.Exists(Path.Combine(_editorModels, "checkpoints", "base.safetensors")), Is.False, "No link is made.");
        });
    }

    [Test]
    public void Redirect_NeverRemovesALinkItDidNotMake()
    {
        // The projector's link to the owner's model of the same name, pointing where this library's would.
        WriteLibraryFile("checkpoints/base.safetensors", 4);
        var editorPath = Path.Combine(_editorModels, "checkpoints", "base.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(editorPath)!);
        File.CreateSymbolicLink(editorPath, Path.Combine(_library, "checkpoints", "base.safetensors"));

        CreateLibrary().Redirect([Remote("checkpoints/base.safetensors", 8)], Session);

        Assert.That(new FileInfo(editorPath).LinkTarget, Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
    }

    [Test]
    public void DiscardAll_EmptiesTheDownloadFolderOnly()
    {
        var library = CreateLibrary();
        var kept = WriteLibraryFile("checkpoints/base.safetensors", 8);
        var staging = library.GetStagingPath(Path.Combine(_library, "loras", "partial.safetensors"))!;
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllBytes(staging, new byte[3]);

        library.DiscardAll();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(staging), Is.False);
            Assert.That(File.Exists(kept), Is.True);
        });
    }

    [Test]
    public void DiscardAll_RemovesLinksInTheDownloadFolderButNeverWhatTheyPointAt()
    {
        var library = CreateLibrary();
        var staging = Stage(library, Path.Combine(_library, "loras", "partial.safetensors"), 3);
        var key = Path.GetDirectoryName(staging)!;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside", "nested")).FullName;
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "not the runner's");
        Directory.CreateSymbolicLink(Path.Combine(key, "folder-link"), outside);
        File.CreateSymbolicLink(Path.Combine(key, "file-link"), sentinel);
        Directory.CreateDirectory(Path.Combine(key, "deeper", "still"));
        File.WriteAllText(Path.Combine(key, "deeper", "still", "part.bin"), "part");

        library.DiscardAll();

        var runnerFolder = Path.GetDirectoryName(key)!;
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(runnerFolder), Is.True, "This runner's folder stays.");
            Assert.That(Directory.EnumerateFileSystemEntries(runnerFolder), Is.Empty);
            Assert.That(File.Exists(sentinel), Is.True, "Nothing a link points at is deleted.");
        });
    }

    [Test]
    public void DiscardAll_NeverFollowsTheRunnersFolderSwappedForALink()
    {
        var library = CreateLibrary();
        var staging = Stage(library, Path.Combine(_library, "loras", "partial.safetensors"), 3);
        var runnerFolder = Path.GetDirectoryName(Path.GetDirectoryName(staging)!)!;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "not the runner's");
        Directory.Delete(runnerFolder, recursive: true);
        Directory.CreateSymbolicLink(runnerFolder, outside);

        library.DiscardAll();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(sentinel), Is.True, "Nothing outside the models folder is deleted.");
            Assert.That(new DirectoryInfo(runnerFolder).LinkTarget, Is.EqualTo(outside));
        });
    }

    // Staged in the models folder, the download could not be moved onto another mount: another filesystem,
    // or a folder of the same one bound there from elsewhere.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ADownloadForAFolderMountedThere_IsDownloadedInsideTheContainer(bool boundFromTheSameFilesystem)
    {
        var checkpoints = Directory.CreateDirectory(Path.Combine(_library, "checkpoints")).FullName;
        var mounted = boundFromTheSameFilesystem
            ? TryRun("mount", "--bind", Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName, checkpoints)
            : TryRun("mount", "-t", "tmpfs", "tmpfs", checkpoints);
        if (!mounted)
        {
            Assert.Ignore("Mounting a filesystem needs CAP_SYS_ADMIN, which this container does not have.");
        }

        try
        {
            var backend = new WritingBackend();
            var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
            await using var service = new ModelDownloadService(
                backend,
                settings,
                destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
                library: CreateLibrary());
            await service.InitializeAsync(CancellationToken.None);
            service.SetActiveSession(Session);

            await await service.PreparePromptDownloadsAsync(
                [Remote("checkpoints/base.safetensors", 8)],
                CancellationToken.None,
                highPriority: true);

            var editorPath = new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors"));
            Assert.Multiple(() =>
            {
                Assert.That(editorPath.LinkTarget, Is.Null);
                Assert.That(editorPath.Length, Is.EqualTo(8), "The editor opens the finished model.");
                Assert.That(Directory.EnumerateFileSystemEntries(checkpoints), Is.Empty);
            });
        }
        finally
        {
            TryRun("umount", checkpoints);
        }
    }

    private static bool TryRun(string fileName, params string[] arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true
            })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Test]
    public async Task APromptsDownload_IsSavedInTheFolderAndLinkedForTheEditor()
    {
        var backend = new WritingBackend();
        var settings = Aria2Settings.FromEnvironment(name => name == "ARIA2_STATE_DIR" ? Path.Combine(_root, "aria2-state") : null, static () => "generated-secret");
        await using var service = new ModelDownloadService(
            backend,
            settings,
            destinationPolicy: new ModelDestinationPolicy([_editorModels, _library]),
            library: CreateLibrary());
        await service.InitializeAsync(CancellationToken.None);
        service.SetActiveSession("session-1");
        var request = Remote("checkpoints/base.safetensors", 8);

        service.RegisterDownloads("session-1", [request]);
        var demand = await service.PreparePromptDownloadsAsync([request], CancellationToken.None, highPriority: true);
        await demand;

        var saved = Path.Combine(_library, "checkpoints", "base.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(saved).Length, Is.EqualTo(8), "Saved in the owner's folder.");
            Assert.That(backend.Destinations, Has.All.Contains(ModelLibrary.StagingFolderName), "Written in the download folder first.");
            Assert.That(new FileInfo(Path.Combine(_editorModels, "checkpoints", "base.safetensors")).LinkTarget, Is.EqualTo(saved));
            Assert.That(
                Directory.EnumerateFiles(Path.Combine(_library, ModelLibrary.StagingFolderName), "*", SearchOption.AllDirectories)
                    .Where(static file => !file.EndsWith(".lease", StringComparison.Ordinal)),
                Is.Empty,
                "Nothing half downloaded is left.");
        });
    }

    /// <summary>Writes each transfer whole at once, where it is asked to.</summary>
    private sealed class WritingBackend : TestModelTransferBackend
    {
        private readonly Dictionary<ModelTransferHandle, (string Path, long Length)> _transfers = [];

        public List<string> Destinations { get; } = [];

        public override Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(ModelTransferCreateRequest request, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            File.WriteAllBytes(request.DestinationPath, new byte[request.ExpectedLength]);
            Destinations.Add(request.DestinationPath);
            var handle = new ModelTransferHandle(request.PreferredIdentifier ?? $"gid-{_transfers.Count + 1}");
            _transfers[handle] = (request.DestinationPath, request.ExpectedLength);
            IReadOnlyList<ModelTransferHandle> handles = [handle];
            return Task.FromResult(handles);
        }

        public override Task<ModelTransferSnapshot> GetStatusAsync(ModelTransferHandle handle, CancellationToken cancellationToken)
            => _transfers.TryGetValue(handle, out var transfer)
                ? Task.FromResult(Snapshot(
                    handle,
                    "complete",
                    transfer.Length,
                    transfer.Length,
                    files: [new ModelTransferFileSnapshot(transfer.Path, transfer.Length, transfer.Length, ["https://models.example.test/model"])]))
                : throw new ModelTransferStatusUnavailableException($"Transfer '{handle}' is unavailable.");
    }

    private ModelDownloadRequest Remote(string relativePath, long size) => new()
    {
        ModelId = "model-" + Path.GetFileNameWithoutExtension(relativePath),
        Filename = Path.GetFileName(relativePath),
        DestinationPath = Path.Combine(_editorModels, relativePath),
        SizeBytes = size,
        SourceUrl = "https://models.example.test/" + Path.GetFileName(relativePath),
    };

    private string WriteLibraryFile(string relativePath, int size)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
}
