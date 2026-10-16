using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offloadr.Runner.Core.Sealing;

namespace Offloadr.Runner.Tests;

/// <summary>
/// Interop with build/sealed-envelope-vectors.mjs, which implements the format on WebCrypto only.
/// WebCrypto to .NET uses the committed vectors; .NET to WebCrypto needs node on PATH.
/// </summary>
public class SealedEnvelopeInteropTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static string VectorsPath
        => Path.Combine(RepositoryRoot, "tests", "Offloadr.Runner.Tests", "Sealing", "vectors", "webcrypto-vectors.json");

    private static string ScriptPath => Path.Combine(RepositoryRoot, "build", "sealed-envelope-vectors.mjs");

    private static IEnumerable<TestCaseData> WebCryptoVectors()
    {
        var document = JsonNode.Parse(File.ReadAllText(VectorsPath))!;
        foreach (var vector in document["vectors"]!.AsArray())
        {
            yield return new TestCaseData(vector!.ToJsonString()).SetArgDisplayNames(vector["name"]!.GetValue<string>());
        }
    }

    [Test]
    public void WebCryptoVectors_UseTheCurrentAlgorithmAndCoverEveryIdentityKind()
    {
        var document = JsonNode.Parse(File.ReadAllText(VectorsPath))!;
        var kinds = document["vectors"]!.AsArray().Select(vector => vector!["identity"]!["kind"]!.GetValue<string>());

        Assert.Multiple(() =>
        {
            Assert.That(document["algorithm"]!.GetValue<string>(), Is.EqualTo(SealedEnvelopeFormat.AlgorithmId));
            Assert.That(kinds.Distinct(), Is.EquivalentTo(new[] { "artifact", "workspace_file", "seed_archive" }));
        });
    }

    [TestCaseSource(nameof(WebCryptoVectors))]
    public async Task WebCryptoVector_OpensInDotNet(string json)
    {
        var vector = JsonNode.Parse(json)!;
        using var recipient = ImportRecipient(vector["recipient"]!);
        var identity = ParseIdentity(vector["identity"]!);
        var envelope = ParseEnvelope(vector["envelope"]!);
        var output = new MemoryStream();

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, identity, output);
        await reader.WriteAsync(Convert.FromBase64String(vector["ciphertext"]!.GetValue<string>()));
        await reader.CompleteAsync();

        Assert.Multiple(() =>
        {
            Assert.That(reader.Header.PlaintextSize, Is.EqualTo(vector["plaintext_size"]!.GetValue<long>()));
            Assert.That(
                Convert.ToHexStringLower(SHA256.HashData(output.ToArray())),
                Is.EqualTo(vector["plaintext_sha256"]!.GetValue<string>()));
        });
    }

    [TestCaseSource(nameof(WebCryptoVectors))]
    public void WebCryptoVector_RejectsAnotherIdentity(string json)
    {
        var vector = JsonNode.Parse(json)!;
        using var recipient = ImportRecipient(vector["recipient"]!);
        SealedObjectIdentity identity = ParseIdentity(vector["identity"]!) switch
        {
            SealedArtifactIdentity artifact => artifact with { SessionId = "session-0002" },
            SealedWorkspaceFileIdentity file => file with { SessionId = "session-0002" },
            SealedSeedArchiveIdentity archive => archive with { SessionId = "session-0002" },
            var other => throw new AssertionException($"Unexpected identity {other}"),
        };

        Assert.That(
            () => SealedEnvelopeReader.Open(ParseEnvelope(vector["envelope"]!), recipient, identity, new MemoryStream()),
            Throws.InstanceOf<SealedEnvelopeException>());
    }

    [Test]
    public async Task DotNetVectors_OpenInWebCrypto()
    {
        var node = RequireNode();
        var directory = Directory.CreateTempSubdirectory("sealed-envelope-");
        try
        {
            var path = Path.Combine(directory.FullName, "dotnet-vectors.json");
            var document = await CreateDotNetVectorsAsync();
            await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var (exitCode, output) = await RunAsync(node, ScriptPath, "decrypt", path);

            Assert.That(exitCode, Is.Zero, output);
            Assert.That(output, Does.Contain("ok: artifact-empty, workspace-file-1, seed-archive-65536, artifact-65537, artifact-131072"));

            // The script must actually check: a flipped ciphertext bit fails there too.
            var tampered = document["vectors"]![3]!;
            var ciphertext = Convert.FromBase64String(tampered["ciphertext"]!.GetValue<string>());
            ciphertext[^1] ^= 0x01;
            tampered["ciphertext"] = Convert.ToBase64String(ciphertext);
            await File.WriteAllTextAsync(path, document.ToJsonString());

            (exitCode, output) = await RunAsync(node, ScriptPath, "decrypt", path);

            Assert.That(exitCode, Is.Not.Zero, output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<JsonObject> CreateDotNetVectorsAsync()
    {
        using var recipient = SealingKeyPair.Generate("dotnet-recipient-1");
        var privateKey = recipient.ExportPrivateKey();
        var samples = new (string Name, int Size, SealedObjectIdentity Identity)[]
        {
            ("artifact-empty", 0, new SealedArtifactIdentity("session-0001", "editor-0001", "output", "", "empty.txt")),
            ("workspace-file-1", 1, new SealedWorkspaceFileIdentity("session-0001", "user", "default/workflows/räksmörgås.json")),
            ("seed-archive-65536", 65536, new SealedSeedArchiveIdentity("session-0001", "input")),
            ("artifact-65537", 65537, new SealedArtifactIdentity("session-0001", "editor-0001", "temp", "a/b", "x.webp")),
            ("artifact-131072", 131072, new SealedArtifactIdentity("session-0001", "editor-0001", "output", "", "y.png")),
        };

        var vectors = new JsonArray();
        foreach (var (name, size, identity) in samples)
        {
            var plaintext = RandomNumberGenerator.GetBytes(size);
            var (envelope, ciphertext) = await SealedEnvelopeTests.SealAsync(recipient.PublicKey, identity, plaintext);
            vectors.Add(new JsonObject
            {
                ["name"] = name,
                ["recipient"] = new JsonObject
                {
                    ["key_id"] = recipient.KeyId,
                    ["public_key"] = Convert.ToHexStringLower(recipient.ExportPublicKey()),
                    ["private_key"] = Convert.ToHexStringLower(privateKey),
                },
                ["identity"] = IdentityJson(identity),
                ["plaintext_size"] = size,
                ["plaintext_sha256"] = Convert.ToHexStringLower(SHA256.HashData(plaintext)),
                ["envelope"] = new JsonObject
                {
                    ["header"] = Convert.ToHexStringLower(envelope.Header.Span),
                    ["encapsulated_key"] = Convert.ToHexStringLower(envelope.EncapsulatedKey.Span),
                    ["sealed_data_key"] = Convert.ToHexStringLower(envelope.SealedDataKey.Span),
                },
                ["ciphertext"] = Convert.ToBase64String(ciphertext),
            });
        }

        CryptographicOperations.ZeroMemory(privateKey);
        return new JsonObject
        {
            ["format"] = "offloadr-sealed-envelope-vectors",
            ["algorithm"] = SealedEnvelopeFormat.AlgorithmId,
            ["generator"] = "Offloadr.Runner.Core.Sealing (.NET)",
            ["vectors"] = vectors,
        };
    }

    private static SealingKeyPair ImportRecipient(JsonNode recipient)
        => SealingKeyPair.Import(
            recipient["key_id"]!.GetValue<string>(),
            Convert.FromHexString(recipient["private_key"]!.GetValue<string>()),
            Convert.FromHexString(recipient["public_key"]!.GetValue<string>()));

    private static SealedEnvelope ParseEnvelope(JsonNode envelope)
        => new(
            Convert.FromHexString(envelope["header"]!.GetValue<string>()),
            Convert.FromHexString(envelope["encapsulated_key"]!.GetValue<string>()),
            Convert.FromHexString(envelope["sealed_data_key"]!.GetValue<string>()));

    private static SealedObjectIdentity ParseIdentity(JsonNode identity)
    {
        string Field(string name) => identity[name]!.GetValue<string>();
        return Field("kind") switch
        {
            "artifact" => new SealedArtifactIdentity(Field("session_id"), Field("editor_sid"), Field("type"), Field("subfolder"), Field("filename")),
            "workspace_file" => new SealedWorkspaceFileIdentity(Field("session_id"), Field("root"), Field("relative_path")),
            "seed_archive" => new SealedSeedArchiveIdentity(Field("session_id"), Field("root")),
            var kind => throw new AssertionException($"Unknown identity kind {kind}"),
        };
    }

    private static JsonObject IdentityJson(SealedObjectIdentity identity) => identity switch
    {
        SealedArtifactIdentity a => new JsonObject
        {
            ["kind"] = "artifact",
            ["session_id"] = a.SessionId,
            ["editor_sid"] = a.EditorSid,
            ["type"] = a.Type,
            ["subfolder"] = a.Subfolder,
            ["filename"] = a.Filename,
        },
        SealedWorkspaceFileIdentity w => new JsonObject
        {
            ["kind"] = "workspace_file",
            ["session_id"] = w.SessionId,
            ["root"] = w.Root,
            ["relative_path"] = w.RelativePath,
        },
        SealedSeedArchiveIdentity s => new JsonObject
        {
            ["kind"] = "seed_archive",
            ["session_id"] = s.SessionId,
            ["root"] = s.Root,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(identity)),
    };

    private static string RequireNode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe" } : new[] { "node", "nodejs" };
        var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        const string message = "node is not on PATH; the .NET to WebCrypto direction is not checked.";
        if (string.Equals(Environment.GetEnvironmentVariable("RUNNER_NATIVE_TESTS_REQUIRED"), "1", StringComparison.Ordinal))
        {
            Assert.Fail(message);
        }

        Assert.Ignore(message);
        return string.Empty;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await standardOutput + await standardError);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "runner.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
