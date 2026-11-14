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
    private ModelLibrary CreateLibrary() => new(_library, _library, _editorModels);

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
            Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(_library, ModelLibrary.StagingFolderName)), Is.Empty);
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
