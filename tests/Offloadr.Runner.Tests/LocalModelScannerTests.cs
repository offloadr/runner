using Offloadr.Common.V1;

namespace Offloadr.Runner.Tests;

public class LocalModelScannerTests
{
    [Test]
    public void Scan_DiscoversSupportedFiles_WithOriginAndCategories()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "loras"));
            Directory.CreateDirectory(Path.Combine(root, "vae"));
            File.WriteAllText(Path.Combine(root, "loras", "style.safetensors"), "lora");
            File.WriteAllText(Path.Combine(root, "vae", "base.pt"), "vae");
            File.WriteAllText(Path.Combine(root, "loras", "ignore.txt"), "nope");

            var scanner = new LocalModelScanner("runner-1", root);
            var snapshot = scanner.Scan();

            Assert.That(snapshot.Models.Count, Is.EqualTo(2));
            Assert.That(snapshot.Models.All(m => m.Origin == ModelOrigin.LocalRunner), Is.True);
            Assert.That(snapshot.Models.All(m => m.OriginRunnerId == "runner-1"), Is.True);

            var lora = snapshot.Models.Single(m => m.Filename == "style.safetensors");
            var vae = snapshot.Models.Single(m => m.Filename == "base.pt");
            Assert.That(lora.Category, Is.EqualTo(ModelCategory.Lora));
            Assert.That(vae.Category, Is.EqualTo(ModelCategory.Vae));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_GivesCaseDistinctFilesDistinctModelIds()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "checkpoints"));
            File.WriteAllText(Path.Combine(root, "checkpoints", "Foo.safetensors"), "same");
            File.WriteAllText(Path.Combine(root, "checkpoints", "foo.safetensors"), "same");

            var snapshot = new LocalModelScanner("runner-1", root).Scan();

            Assert.That(snapshot.Models.Select(static model => model.ModelId).Distinct().Count(), Is.EqualTo(2));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_DoesNotOfferASelectionHashSharedByTwoFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "checkpoints", "a"));
            Directory.CreateDirectory(Path.Combine(root, "checkpoints", "b"));
            File.WriteAllText(Path.Combine(root, "checkpoints", "a", "model.safetensors"), "aaaa");
            File.WriteAllText(Path.Combine(root, "checkpoints", "b", "model.safetensors"), "bbbb");
            File.WriteAllText(Path.Combine(root, "checkpoints", "unique.safetensors"), "cccc");

            var snapshot = new LocalModelScanner("runner-1", root).Scan();
            var sharedHash = LocalModelSelectionHash.Compute("model.safetensors", 4, ModelCategory.Checkpoint);
            var uniqueHash = LocalModelSelectionHash.Compute("unique.safetensors", 4, ModelCategory.Checkpoint);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Models, Has.Count.EqualTo(3));
                Assert.That(snapshot.SelectionHashes, Does.Not.Contain(sharedHash));
                Assert.That(snapshot.SelectionHashes, Does.Contain(uniqueHash));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_SkipsSymbolicLinksToFilesAndFolders()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "loras"));
            File.WriteAllText(Path.Combine(root, "loras", "real.safetensors"), "lora");
            File.WriteAllText(Path.Combine(outside, "host-secret.safetensors"), "secret");
            Directory.CreateDirectory(Path.Combine(outside, "vae"));
            File.WriteAllText(Path.Combine(outside, "vae", "aliased.pt"), "secret");
            File.CreateSymbolicLink(Path.Combine(root, "loras", "linked.safetensors"), Path.Combine(outside, "host-secret.safetensors"));
            Directory.CreateSymbolicLink(Path.Combine(root, "vae"), Path.Combine(outside, "vae"));

            var snapshot = new LocalModelScanner("runner-1", root).Scan();

            Assert.That(snapshot.Models.Select(static model => model.Filename), Is.EqualTo(new[] { "real.safetensors" }));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Test]
    public void Scan_ProducesStableDigestAndModelIds_ForUnchangedFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "checkpoints"));
            File.WriteAllText(Path.Combine(root, "checkpoints", "model.ckpt"), "checkpoint");
            var scanner = new LocalModelScanner("runner-2", root);

            var first = scanner.Scan();
            var second = scanner.Scan();

            Assert.That(first.Digest, Is.EqualTo(second.Digest));
            Assert.That(first.SelectionDigest, Is.EqualTo(second.SelectionDigest));
            Assert.That(first.Models.Count, Is.EqualTo(1));
            Assert.That(second.Models.Count, Is.EqualTo(1));
            Assert.That(first.Models[0].ModelId, Is.EqualTo(second.Models[0].ModelId));
            Assert.That(first.SelectionHashes, Is.EqualTo(second.SelectionHashes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_PathMoveChangesProjectionDigest_ButPreservesRemoteSelectionDigest()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "checkpoints"));
            Directory.CreateDirectory(Path.Combine(root, "archive"));
            var sourcePath = Path.Combine(root, "checkpoints", "model.ckpt");
            var movedPath = Path.Combine(root, "archive", "model.ckpt");
            File.WriteAllText(sourcePath, "checkpoint");
            var scanner = new LocalModelScanner("runner-2", root);

            var first = scanner.Scan();
            File.Move(sourcePath, movedPath);
            var second = scanner.Scan();

            Assert.Multiple(() =>
            {
                Assert.That(first.Digest, Is.Not.EqualTo(second.Digest));
                Assert.That(first.SelectionDigest, Is.EqualTo(second.SelectionDigest));
                Assert.That(first.SelectionHashes, Is.EqualTo(second.SelectionHashes));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_ComputesFolderStats()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "loras"));
            Directory.CreateDirectory(Path.Combine(root, "vae"));
            File.WriteAllText(Path.Combine(root, "loras", "a.safetensors"), "1234");
            File.WriteAllText(Path.Combine(root, "loras", "b.safetensors"), "12");
            File.WriteAllText(Path.Combine(root, "vae", "v.pt"), "1");

            var scanner = new LocalModelScanner("runner-3", root);
            var snapshot = scanner.Scan();

            Assert.That(snapshot.FolderStats.Count, Is.EqualTo(2));
            var loras = snapshot.FolderStats.Single(stat => stat.Folder == "loras");
            var vae = snapshot.FolderStats.Single(stat => stat.Folder == "vae");
            Assert.That(loras.ModelCount, Is.EqualTo(2));
            Assert.That(vae.ModelCount, Is.EqualTo(1));
            Assert.That(loras.TotalBytes, Is.GreaterThan(vae.TotalBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_RecognizesForgeNeoModelFolders()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Stable-diffusion"));
            Directory.CreateDirectory(Path.Combine(root, "ESRGAN"));
            Directory.CreateDirectory(Path.Combine(root, "VAE-approx"));
            File.WriteAllText(Path.Combine(root, "Stable-diffusion", "model.safetensors"), "checkpoint");
            File.WriteAllText(Path.Combine(root, "ESRGAN", "upscale.pth"), "upscaler");
            File.WriteAllText(Path.Combine(root, "VAE-approx", "vaeapprox.pt"), "vaeapprox");

            var scanner = new LocalModelScanner("runner-forge", root);
            var snapshot = scanner.Scan();

            Assert.That(snapshot.Models.Single(model => model.Filename == "model.safetensors").Category, Is.EqualTo(ModelCategory.Checkpoint));
            Assert.That(snapshot.Models.Single(model => model.Filename == "upscale.pth").Category, Is.EqualTo(ModelCategory.Upscaler));
            Assert.That(snapshot.Models.Single(model => model.Filename == "vaeapprox.pt").Category, Is.EqualTo(ModelCategory.VaeApprox));
            Assert.That(snapshot.Models.Single(model => model.Filename == "upscale.pth").LocalPath, Does.EndWith("ESRGAN"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_RecognizesExpandedComfyFolders()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "clip_vision"));
            Directory.CreateDirectory(Path.Combine(root, "style_models"));
            Directory.CreateDirectory(Path.Combine(root, "vae_approx"));
            Directory.CreateDirectory(Path.Combine(root, "model_patches"));
            Directory.CreateDirectory(Path.Combine(root, "audio_encoders"));
            Directory.CreateDirectory(Path.Combine(root, "background_removal"));
            Directory.CreateDirectory(Path.Combine(root, "frame_interpolation"));
            Directory.CreateDirectory(Path.Combine(root, "geometry_estimation"));
            Directory.CreateDirectory(Path.Combine(root, "optical_flow"));
            Directory.CreateDirectory(Path.Combine(root, "detection"));
            Directory.CreateDirectory(Path.Combine(root, "t2i_adapter"));
            Directory.CreateDirectory(Path.Combine(root, "unet"));
            Directory.CreateDirectory(Path.Combine(root, "clip"));

            File.WriteAllText(Path.Combine(root, "clip_vision", "vision.safetensors"), "vision");
            File.WriteAllText(Path.Combine(root, "style_models", "style.safetensors"), "style");
            File.WriteAllText(Path.Combine(root, "vae_approx", "taesd_decoder.pth"), "approx");
            File.WriteAllText(Path.Combine(root, "model_patches", "patch.safetensors"), "patch");
            File.WriteAllText(Path.Combine(root, "audio_encoders", "audio.safetensors"), "audio");
            File.WriteAllText(Path.Combine(root, "background_removal", "birefnet.safetensors"), "bg");
            File.WriteAllText(Path.Combine(root, "frame_interpolation", "rife.pth"), "rife");
            File.WriteAllText(Path.Combine(root, "geometry_estimation", "geo.onnx"), "geo");
            File.WriteAllText(Path.Combine(root, "optical_flow", "flow.onnx"), "flow");
            File.WriteAllText(Path.Combine(root, "detection", "detector.onnx"), "detector");
            File.WriteAllText(Path.Combine(root, "t2i_adapter", "adapter.safetensors"), "adapter");
            File.WriteAllText(Path.Combine(root, "unet", "unet.safetensors"), "unet");
            File.WriteAllText(Path.Combine(root, "clip", "clip_l.safetensors"), "clip");

            var scanner = new LocalModelScanner("runner-comfy", root);
            var snapshot = scanner.Scan();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Models.Single(model => model.Filename == "vision.safetensors").Category, Is.EqualTo(ModelCategory.ClipVision));
                Assert.That(snapshot.Models.Single(model => model.Filename == "style.safetensors").Category, Is.EqualTo(ModelCategory.StyleModel));
                Assert.That(snapshot.Models.Single(model => model.Filename == "taesd_decoder.pth").Category, Is.EqualTo(ModelCategory.VaeApprox));
                Assert.That(snapshot.Models.Single(model => model.Filename == "patch.safetensors").Category, Is.EqualTo(ModelCategory.ModelPatch));
                Assert.That(snapshot.Models.Single(model => model.Filename == "audio.safetensors").Category, Is.EqualTo(ModelCategory.AudioEncoder));
                Assert.That(snapshot.Models.Single(model => model.Filename == "birefnet.safetensors").Category, Is.EqualTo(ModelCategory.BackgroundRemoval));
                Assert.That(snapshot.Models.Single(model => model.Filename == "rife.pth").Category, Is.EqualTo(ModelCategory.FrameInterpolation));
                Assert.That(snapshot.Models.Single(model => model.Filename == "geo.onnx").Category, Is.EqualTo(ModelCategory.GeometryEstimation));
                Assert.That(snapshot.Models.Single(model => model.Filename == "flow.onnx").Category, Is.EqualTo(ModelCategory.OpticalFlow));
                Assert.That(snapshot.Models.Single(model => model.Filename == "detector.onnx").Category, Is.EqualTo(ModelCategory.Detection));
                Assert.That(snapshot.Models.Single(model => model.Filename == "adapter.safetensors").Category, Is.EqualTo(ModelCategory.Controlnet));
                Assert.That(snapshot.Models.Single(model => model.Filename == "unet.safetensors").Category, Is.EqualTo(ModelCategory.DiffusionModel));
                Assert.That(snapshot.Models.Single(model => model.Filename == "clip_l.safetensors").Category, Is.EqualTo(ModelCategory.TextEncoder));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void Scan_HandlesClassifierAndConfigExtensionRules()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "classifiers"));
            Directory.CreateDirectory(Path.Combine(root, "configs"));
            File.WriteAllText(Path.Combine(root, "classifiers", "foo"), "classifier");
            File.WriteAllText(Path.Combine(root, "configs", "foo.yaml"), "yaml");
            File.WriteAllText(Path.Combine(root, "configs", "foo.yml"), "yml");
            File.WriteAllText(Path.Combine(root, "configs", "foo.safetensors"), "wrong");

            var scanner = new LocalModelScanner("runner-configs", root);
            var snapshot = scanner.Scan();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Models.Single(model => model.Filename == "foo").Category, Is.EqualTo(ModelCategory.Classifier));
                Assert.That(snapshot.Models.Single(model => model.Filename == "foo.yaml").Category, Is.EqualTo(ModelCategory.Config));
                Assert.That(snapshot.Models.Any(model => model.Filename == "foo.yml"), Is.False);
                Assert.That(snapshot.Models.Any(model => model.Filename == "foo.safetensors"), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-model-scanner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort test cleanup.
        }
    }
}
