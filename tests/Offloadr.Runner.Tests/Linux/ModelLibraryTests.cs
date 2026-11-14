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

    // The read-only view is the same folder here; in the runner it is a second, read-only mount of it.
    private ModelLibrary CreateLibrary(string runnerId = "11111111-1111-4111-8111-111111111111") => new(_library, _library, _editorModels, runnerId);

    [Test]
    public void Redirect_SendsADownloadToTheFolderAndLinksTheEditorsPathThere()
    {
        var request = Remote("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([request]).Single();

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

        var first = library.Redirect([request]).Single();
        var second = library.Redirect([request]).Single();

        Assert.That(second.DestinationPath, Is.EqualTo(first.DestinationPath));
    }

    [Test]
    public async Task Redirect_FromPromptsAtOnce_SendsThemAllToTheSameDownload()
    {
        var library = CreateLibrary();
        var request = Remote("checkpoints/base.safetensors", 8);

        var prompts = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => library.Redirect([request]).Single())));

        Assert.That(
            prompts.Select(static redirected => redirected.DestinationPath),
            Has.All.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")),
            "None falls back to downloading over another's link.");
    }

    [Test]
    public void Redirect_CountsAFileOfTheRequestedSizeAsTheModel()
    {
        WriteLibraryFile("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([Remote("checkpoints/base.safetensors", 8)]).Single();

        Assert.That(redirected.DestinationPath, Is.EqualTo(Path.Combine(_library, "checkpoints", "base.safetensors")));
    }

    [Test]
    public void Redirect_LeavesTheOwnersFileOfAnotherSizeAndDownloadsInsideTheContainer()
    {
        WriteLibraryFile("checkpoints/base.safetensors", 16);
        var request = Remote("checkpoints/base.safetensors", 8);

        var redirected = CreateLibrary().Redirect([request]).Single();

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

        var redirected = CreateLibrary().Redirect([request]).Single();

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

        var redirected = CreateLibrary().Redirect([local, unknownSize, elsewhere]);

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
        var destination = WriteLibraryFile("loras/style.safetensors", 4);
        var staging = library.GetStagingPath(destination)!;
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllBytes(staging, new byte[8]);

        Assert.That(() => library.Publish(staging, destination), Throws.InstanceOf<IOException>());
        Assert.That(new FileInfo(destination).Length, Is.EqualTo(4), "The owner's file is kept.");
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
    public void Redirect_OfAnotherSizeAtALinkedPath_RemovesTheLinkSoItDownloadsInTheContainer()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)]);
        WriteLibraryFile("checkpoints/base.safetensors", 8);
        var other = Remote("checkpoints/base.safetensors", 16);

        var redirected = library.Redirect([other]).Single();

        var editorPath = Path.Combine(_editorModels, "checkpoints", "base.safetensors");
        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(editorPath));
            Assert.That(File.Exists(editorPath) || new FileInfo(editorPath).LinkTarget is not null, Is.False, "Its link to the saved model is gone.");
            Assert.That(new FileInfo(Path.Combine(_library, "checkpoints", "base.safetensors")).Length, Is.EqualTo(8), "The saved model stays.");
        });
    }

    [Test]
    public void LibraryPathFor_FindsTheDownloadBehindTheEditorsLinkOnly()
    {
        var library = CreateLibrary();
        library.Redirect([Remote("checkpoints/base.safetensors", 8)]);
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
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllBytes(staging, new byte[size]);
        return staging;
    }

    [Test]
    public void Discard_NeverFollowsADownloadFolderSwappedForALink()
    {
        var library = CreateLibrary();
        var destination = Path.Combine(_library, "loras", "style.safetensors");
        var staging = library.GetStagingPath(destination)!;
        var runnerFolder = Path.GetDirectoryName(Path.GetDirectoryName(staging)!)!;
        var downloadFolder = Path.GetDirectoryName(runnerFolder)!;

        // Elsewhere, a tree laid out like this runner's downloads, and the download folder pointed at it.
        var outside = Path.Combine(_root, "outside");
        var outsideKey = Path.Combine(outside, Path.GetFileName(runnerFolder), Path.GetFileName(Path.GetDirectoryName(staging)!));
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

        var redirected = CreateLibrary().Redirect([request]).Single();

        Assert.Multiple(() =>
        {
            Assert.That(redirected.DestinationPath, Is.EqualTo(request.DestinationPath), "Downloaded inside the container instead.");
            Assert.That(Directory.Exists(folder), Is.True);
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

        CreateLibrary().Redirect([Remote("checkpoints/base.safetensors", 8)]);

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
                Directory.EnumerateFiles(Path.Combine(_library, ModelLibrary.StagingFolderName), "*", SearchOption.AllDirectories),
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
