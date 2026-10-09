using Offloadr.Common.V1;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Offloadr.Runner.Linux;

public readonly record struct LocalModelFolderStat(string Folder, int ModelCount, long TotalBytes);
public readonly record struct LocalModelSnapshot(
    IReadOnlyList<ModelInfo> Models,
    string Digest,
    IReadOnlyList<LocalModelFolderStat> FolderStats,
    IReadOnlyList<string> SelectionHashes,
    string SelectionDigest);

internal sealed class LocalModelScanner
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors",
        ".ckpt",
        ".pt",
        ".pth",
        ".bin",
        ".onnx",
        ".emb",
        ".gguf"
    };

    private static readonly Dictionary<string, ModelCategory> CategoryFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["checkpoints"] = ModelCategory.Checkpoint,
        ["checkpoint"] = ModelCategory.Checkpoint,
        ["stable-diffusion"] = ModelCategory.Checkpoint,
        ["diffusion_models"] = ModelCategory.DiffusionModel,
        ["diffusion_model"] = ModelCategory.DiffusionModel,
        ["unet"] = ModelCategory.DiffusionModel,
        ["vae"] = ModelCategory.Vae,
        ["vae_approx"] = ModelCategory.VaeApprox,
        ["vae-approx"] = ModelCategory.VaeApprox,
        ["clip"] = ModelCategory.TextEncoder,
        ["text_encoder"] = ModelCategory.TextEncoder,
        ["text_encoders"] = ModelCategory.TextEncoder,
        ["loras"] = ModelCategory.Lora,
        ["lora"] = ModelCategory.Lora,
        ["controlnet"] = ModelCategory.Controlnet,
        ["controlnets"] = ModelCategory.Controlnet,
        ["t2i_adapter"] = ModelCategory.Controlnet,
        ["upscale_models"] = ModelCategory.Upscaler,
        ["upscalers"] = ModelCategory.Upscaler,
        ["esrgan"] = ModelCategory.Upscaler,
        ["embeddings"] = ModelCategory.Embedding,
        ["embedding"] = ModelCategory.Embedding,
        ["latent_upscale_models"] = ModelCategory.LatentUpscaler,
        ["latent_upscalers"] = ModelCategory.LatentUpscaler,
        ["audio_encoders"] = ModelCategory.AudioEncoder,
        ["audio_encoder"] = ModelCategory.AudioEncoder,
        ["clip_vision"] = ModelCategory.ClipVision,
        ["style_models"] = ModelCategory.StyleModel,
        ["style_model"] = ModelCategory.StyleModel,
        ["gligen"] = ModelCategory.Gligen,
        ["hypernetworks"] = ModelCategory.Hypernetwork,
        ["hypernetwork"] = ModelCategory.Hypernetwork,
        ["photomaker"] = ModelCategory.Photomaker,
        ["classifiers"] = ModelCategory.Classifier,
        ["classifier"] = ModelCategory.Classifier,
        ["model_patches"] = ModelCategory.ModelPatch,
        ["model_patch"] = ModelCategory.ModelPatch,
        ["configs"] = ModelCategory.Config,
        ["config"] = ModelCategory.Config,
        ["background_removal"] = ModelCategory.BackgroundRemoval,
        ["frame_interpolation"] = ModelCategory.FrameInterpolation,
        ["geometry_estimation"] = ModelCategory.GeometryEstimation,
        ["optical_flow"] = ModelCategory.OpticalFlow,
        ["detection"] = ModelCategory.Detection
    };

    private readonly string _runnerId;
    private readonly string _rootDirectory;

    public LocalModelScanner(string runnerId, string rootDirectory)
    {
        _runnerId = runnerId ?? throw new ArgumentNullException(nameof(runnerId));
        _rootDirectory = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
    }

    public LocalModelSnapshot Scan()
    {
        if (string.IsNullOrWhiteSpace(_rootDirectory) || !Directory.Exists(_rootDirectory))
        {
            return CreateSnapshot(Array.Empty<ModelInfo>());
        }

        var items = new List<ModelInfo>();
        // Enumeration is lazy, so traversal errors surface while iterating: the whole walk
        // is guarded, and unreadable folders are skipped rather than failing the scan.
        try
        {
            var files = Directory.EnumerateFiles(
                _rootDirectory,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    // Dot-files are scanned as before. Symbolic links are skipped and never followed:
                    // a link could alias a host file outside the local model root into sessions.
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            foreach (var path in files)
            {
                ModelInfo? model = null;
                try
                {
                    model = BuildModelInfo(path);
                }
                catch (Exception ex)
                {
                    RunnerLog.Error<LocalModelScanner>(ex, $"Failed to index local model '{path}': {ex.Message}");
                }

                if (model is not null)
                {
                    items.Add(model);
                }
            }
        }
        catch (Exception ex)
        {
            RunnerLog.Error<LocalModelScanner>(ex, $"Failed enumerating local model directory '{_rootDirectory}': {ex.Message}");
            return CreateSnapshot(Array.Empty<ModelInfo>());
        }

        items.Sort(static (left, right) =>
        {
            var localPathCmp = string.Compare(left.LocalPath, right.LocalPath, StringComparison.OrdinalIgnoreCase);
            if (localPathCmp != 0)
            {
                return localPathCmp;
            }

            return string.Compare(left.Filename, right.Filename, StringComparison.OrdinalIgnoreCase);
        });

        return CreateSnapshot(items);
    }

    private static LocalModelSnapshot CreateSnapshot(IReadOnlyList<ModelInfo> models)
    {
        var selectionHashes = ComputeSelectionHashes(models);
        return new LocalModelSnapshot(
            models,
            ComputeDigest(models),
            ComputeFolderStats(models),
            selectionHashes,
            LocalModelSelectionHash.ComputeSnapshotDigest(selectionHashes));
    }

    private ModelInfo? BuildModelInfo(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_rootDirectory, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var fileName = Path.GetFileName(fullPath);
        var category = InferCategory(relativePath, fileName);
        if (!IsSupportedModelFile(category, fileName))
        {
            return null;
        }

        var localDirectory = Path.GetDirectoryName(fullPath) ?? _rootDirectory;
        var modelId = DeterministicModelId(_runnerId, relativePath);
        var modifiedUtc = File.GetLastWriteTimeUtc(fullPath);
        if (modifiedUtc == DateTime.MinValue || modifiedUtc.Kind != DateTimeKind.Utc)
        {
            modifiedUtc = DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc);
        }

        var fileInfo = new FileInfo(fullPath);
        var size = fileInfo.Exists ? fileInfo.Length : 0;

        return new ModelInfo
        {
            ModelId = modelId,
            Filename = fileName,
            SizeBytes = size,
            Status = ModelStatus.Available,
            AddedUtc = Timestamp.FromDateTime(modifiedUtc),
            Category = category,
            LocalPath = localDirectory,
            Origin = ModelOrigin.LocalRunner,
            OriginRunnerId = _runnerId
        };
    }

    private static bool IsSupportedModelFile(ModelCategory category, string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (category == ModelCategory.Config)
        {
            return string.Equals(ext, ".yaml", StringComparison.OrdinalIgnoreCase);
        }

        if (category == ModelCategory.Classifier && string.IsNullOrWhiteSpace(ext))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(ext) && AllowedExtensions.Contains(ext);
    }

    private static string DeterministicModelId(string runnerId, string relativePath)
    {
        var raw = $"{runnerId}:{relativePath}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var guidBytes = hash[..16].ToArray();
        // Set RFC 4122 UUID metadata on top of deterministic hash bytes:
        // - byte 6 high nibble -> version 5 (0101b, hash-name based UUID style)
        // - byte 8 high bits   -> variant 10b (Leach-Salz/RFC 4122)
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes).ToString();
    }

    private static ModelCategory InferCategory(string relativePath, string fileName)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (CategoryFolders.TryGetValue(segment, out var category))
            {
                return category;
            }
        }

        var lowerName = fileName.ToLowerInvariant();
        if (Regex.IsMatch(lowerName, "(^|[_\\-.])vae([_\\-.]|$)"))
        {
            return ModelCategory.Vae;
        }

        if (Regex.IsMatch(lowerName, "(^|[_\\-.])lora([_\\-.]|$)"))
        {
            return ModelCategory.Lora;
        }

        if (Regex.IsMatch(lowerName, "(^|[_\\-.])control(net)?([_\\-.]|$)"))
        {
            return ModelCategory.Controlnet;
        }

        if (lowerName.Contains("upscale", StringComparison.Ordinal))
        {
            return ModelCategory.Upscaler;
        }

        if (lowerName.Contains("embedding", StringComparison.Ordinal) || Regex.IsMatch(lowerName, "(^|[_\\-.])emb([_\\-.]|$)"))
        {
            return ModelCategory.Embedding;
        }

        if (Regex.IsMatch(lowerName, "(^|[_\\-.])(checkpoint|ckpt)([_\\-.]|$)"))
        {
            return ModelCategory.Checkpoint;
        }

        return ModelCategory.Other;
    }

    private static string ComputeDigest(IReadOnlyList<ModelInfo> models)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var model in models)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(model.ModelId));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(model.Filename));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(model.LocalPath));
            hash.AppendData([0]);
            hash.AppendData(BitConverter.GetBytes(model.SizeBytes));
            hash.AppendData([0]);
            hash.AppendData(BitConverter.GetBytes((int)model.Category));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<string> ComputeSelectionHashes(IReadOnlyList<ModelInfo> models)
    {
        return models
            .Select(model => LocalModelSelectionHash.Compute(model.Filename, model.SizeBytes, model.Category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<LocalModelFolderStat> ComputeFolderStats(IReadOnlyList<ModelInfo> models)
    {
        if (models.Count == 0)
        {
            return [];
        }

        var grouped = models
            .GroupBy(model => ExtractFolderName(model.LocalPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => new LocalModelFolderStat(
                group.Key,
                group.Count(),
                group.Sum(model => model.SizeBytes)))
            .OrderBy(stat => stat.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return grouped;
    }

    private static string ExtractFolderName(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return "(root)";
        }

        var normalized = localPath.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return "(root)";
        }

        return Path.GetFileName(normalized) is { Length: > 0 } leaf ? leaf : "(root)";
    }
}
