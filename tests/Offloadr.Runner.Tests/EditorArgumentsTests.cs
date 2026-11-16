namespace Offloadr.Runner.Tests;

public class EditorArgumentsTests
{
    [TestCase(null, new string[0])]
    [TestCase("   ", new string[0])]
    [TestCase("--use-ck-attention", new[] { "--use-ck-attention" })]
    [TestCase("  --lowvram   --reserve-vram 2 ", new[] { "--lowvram", "--reserve-vram", "2" })]
    [TestCase("--preview-method 'taesd auto'", new[] { "--preview-method", "taesd auto" })]
    [TestCase("--theme \"dark mode\"", new[] { "--theme", "dark mode" })]
    [TestCase("--theme \"say \\\"hi\\\"\"", new[] { "--theme", "say \"hi\"" })]
    [TestCase("--theme a\\ b", new[] { "--theme", "a b" })]
    [TestCase("--theme 'a\\b'", new[] { "--theme", "a\\b" })]
    [TestCase("--theme ''", new[] { "--theme", "" })]
    [TestCase("--fp8=on", new[] { "--fp8=on" })]
    public void TrySplit_SplitsLikeAShell(string? text, string[] expected)
    {
        var split = EditorArguments.TrySplit(text, out var arguments, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(split, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(arguments, Is.EqualTo(expected));
        });
    }

    [TestCase("--theme 'dark", "single quote")]
    [TestCase("--theme \"dark", "double quote")]
    public void TrySplit_RefusesAnUnclosedQuote(string text, string quote)
    {
        var split = EditorArguments.TrySplit(text, out var arguments, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(split, Is.False);
            Assert.That(arguments, Is.Empty);
            Assert.That(error, Does.Contain(quote).And.Contain("not closed"));
        });
    }

    [Test]
    public void TrySplit_RefusesTextLongerThanTheLimit()
    {
        var split = EditorArguments.TrySplit("--a " + new string('x', EditorArguments.MaxLength), out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(split, Is.False);
            Assert.That(error, Does.Contain("longer than"));
        });
    }

    [TestCase("comfyui", "--use-ck-attention")]
    [TestCase("comfyui", "--lowvram --reserve-vram 2 --preview-method auto")]
    [TestCase("comfyui", "--fast fp16_accumulation --cache-lru 10")]
    [TestCase("forge-neo", "--use-ck-attention")]
    [TestCase("forge-neo", "--api-log --uv-symlink --fp8")]
    [TestCase("FORGE-NEO", "--sage")]
    public void Problem_AcceptsEditorTuningFlags(string runtimeKind, string text)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(EditorArguments.Problem(runtimeKind, arguments), Is.Null);
    }

    [Test]
    public void Problem_AcceptsNoArgumentsForAnyRuntime()
        => Assert.That(EditorArguments.Problem("some-future-editor", []), Is.Null);

    [TestCase("comfyui", "--port 9000", "--port is one the runner sets")]
    [TestCase("comfyui", "--port=9000", "--port is one the runner sets")]
    [TestCase("comfyui", "--listen 0.0.0.0", "--listen is one the runner sets")]
    [TestCase("comfyui", "--lowvram --base-directory /tmp", "--base-directory is one the runner sets")]
    [TestCase("comfyui", "--multi-user", "--multi-user is one")]
    [TestCase("comfyui", "--enable-cors-header *", "--enable-cors-header is one")]
    [TestCase("comfyui", "--extra-model-paths-config x.yaml", "--extra-model-paths-config is one")]
    [TestCase("forge-neo", "--share", "--share is one the runner sets")]
    [TestCase("forge-neo", "--data-dir /tmp", "--data-dir is one")]
    [TestCase("forge-neo", "--model-ref=/tmp", "--model-ref is one")]
    [TestCase("forge-neo", "--gradio-auth a:b", "--gradio-auth is one")]
    [TestCase("forge-neo", "--add-stop-route", "--add-stop-route is one")]
    public void Problem_RefusesFlagsTheRunnerSetsOrDependsOn(string runtimeKind, string text, string expected)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(EditorArguments.Problem(runtimeKind, arguments), Does.StartWith(expected));
    }

    [TestCase("--front-end-root /srv")]
    [TestCase("--front-end-root=/srv")]
    [TestCase("--front-end-ro /srv")]
    public void Problem_RefusesComfyUisWebPageFolder(string text)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(EditorArguments.Problem("comfyui", arguments), Does.Contain("--front-end-root").And.Contain("is one the runner sets"));
    }

    [Test]
    public void Problem_AcceptsComfyUisFrontEndVersion()
        => Assert.That(EditorArguments.Problem("comfyui", ["--front-end-version", "Comfy-Org/ComfyUI_frontend@latest"]), Is.Null);

    [TestCase("comfyui", "--help", "--help makes the editor print something and exit")]
    [TestCase("forge-neo", "--help", "--help makes the editor print something and exit")]
    [TestCase("comfyui", "--lowvram --hel", "--hel (short for --help) makes the editor")]
    [TestCase("comfyui", "--quick-test-for-ci", "--quick-test-for-ci makes the editor")]
    [TestCase("comfyui", "--list-feature-flags", "--list-feature-flags makes the editor")]
    [TestCase("forge-neo", "--dump-sysinfo", "--dump-sysinfo makes the editor")]
    public void Problem_RefusesFlagsThatEndTheEditorAtOnce(string runtimeKind, string text, string expected)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(EditorArguments.Problem(runtimeKind, arguments), Does.StartWith(expected));
    }

    [TestCase("comfyui", "-h")]
    [TestCase("forge-neo", "-h")]
    [TestCase("forge-neo", "-f")]
    [TestCase("forge-neo", "--lowvram -fh")]
    public void Problem_RefusesShortOptions(string runtimeKind, string text)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(EditorArguments.Problem(runtimeKind, arguments), Does.Contain("short options are not taken"));
    }

    [Test]
    public void Problem_TakesANegativeNumberAsAValue()
        => Assert.That(EditorArguments.Problem("comfyui", ["--preview-size", "-1"]), Is.Null);

    [TestCase("comfyui", "--lis 0.0.0.0", "--lis (short for --listen)")]
    [TestCase("comfyui", "--output-dir /tmp", "--output-dir (short for --output-directory)")]
    [TestCase("forge-neo", "--sha", "--sha (short for --share)")]
    [TestCase("forge-neo", "--data=/tmp", "--data (short for --data-dir)")]
    public void Problem_RefusesAbbreviationsOfRefusedFlags(string runtimeKind, string text, string expected)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(EditorArguments.Problem(runtimeKind, arguments), Does.StartWith(expected));
    }

    [TestCase("comfyui", "--cuda-device 1", "--cuda-device")]
    [TestCase("comfyui", "--default-device 1", "--default-device")]
    [TestCase("forge-neo", "--gpu-device-id 1", "--gpu-device-id")]
    [TestCase("forge-neo", "--device-id=1", "--device-id")]
    public void Problem_PointsGpuChoiceAtTheGpuUuid(string runtimeKind, string text, string flag)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(
            EditorArguments.Problem(runtimeKind, arguments),
            Does.StartWith($"{flag} chooses the GPU").And.Contain("OFFLOADR_GPU_UUID"));
    }

    [TestCase("comfyui", "--some-new-dir /tmp", "--some-new-dir")]
    [TestCase("forge-neo", "--future-models-path=/tmp", "--future-models-path")]
    [TestCase("forge-neo", "--other-settings-file x.json", "--other-settings-file")]
    public void Problem_RefusesUnknownFlagsThatNameFiles(string runtimeKind, string text, string flag)
    {
        Assert.That(EditorArguments.TrySplit(text, out var arguments, out _), Is.True);

        Assert.That(EditorArguments.Problem(runtimeKind, arguments), Does.StartWith($"{flag} names a file or folder"));
    }

    [Test]
    public void Problem_GatesFlagsByEditorFamily()
    {
        Assert.Multiple(() =>
        {
            // Forge Neo's API switch is the runner's; ComfyUI has no flag of that name to protect.
            Assert.That(EditorArguments.Problem("forge-neo", ["--api"]), Is.Not.Null);
            Assert.That(EditorArguments.Problem("comfyui", ["--api"]), Is.Null);
            Assert.That(EditorArguments.Problem("comfyui", ["--multi-user"]), Is.Not.Null);
            Assert.That(EditorArguments.Problem("forge-neo", ["--multi-user"]), Is.Null);
        });
    }

    [Test]
    public void Problem_RefusesArgumentsForAnUnreviewedRuntime()
        => Assert.That(
            EditorArguments.Problem("some-future-editor", ["--lowvram"]),
            Is.EqualTo("The some-future-editor editor does not take extra arguments."));

    [Test]
    public void Problem_RefusesArgumentsThatDoNotStartWithAFlag()
        => Assert.That(
            EditorArguments.Problem("comfyui", ["lowvram"]),
            Does.StartWith("ComfyUI arguments start with a flag"));

    [Test]
    public void Problem_RefusesControlCharacters()
        => Assert.That(
            EditorArguments.Problem("comfyui", ["--theme", "a\nb"]),
            Does.Contain("control characters"));

    [Test]
    public void Problem_RefusesArgumentsLongerThanTheLimit()
        => Assert.That(
            EditorArguments.Problem("comfyui", ["--theme", new string('x', EditorArguments.MaxLength)]),
            Does.Contain("longer than"));

    [TestCase("comfyui", "RUNNER_EDITOR_ARGS_COMFYUI")]
    [TestCase("forge-neo", "RUNNER_EDITOR_ARGS_FORGE_NEO")]
    [TestCase(" Forge-Neo ", "RUNNER_EDITOR_ARGS_FORGE_NEO")]
    public void EnvironmentVariable_IsNamedAfterTheRuntimeKind(string runtimeKind, string expected)
        => Assert.That(EditorArguments.EnvironmentVariable(runtimeKind), Is.EqualTo(expected));

    [Test]
    public void Describe_KeepsFlagsAndDropsValues()
        => Assert.That(
            EditorArguments.Describe(["--reserve-vram", "2", "--preview-method=secret-ish", "--lowvram"]),
            Is.EqualTo("--reserve-vram --preview-method --lowvram"));

    [Test]
    public void SessionOptions_TakeTheVariableThenTheCommandLine()
    {
        var options = SessionProcessOptions.FromEnvironment(
            name => name switch
            {
                "EDITOR_RUNTIME" => "forge-neo",
                "RUNNER_EDITOR_ARGS_FORGE_NEO" => "--use-ck-attention --reserve-vram 2",
                "RUNNER_EDITOR_ARGS_COMFYUI" => "--port 1",
                _ => null,
            },
            ["--fp8"]);

        Assert.Multiple(() =>
        {
            Assert.That(options.EditorArguments, Is.EqualTo(new[] { "--use-ck-attention", "--reserve-vram", "2", "--fp8" }));
            Assert.That(options.EditorArgumentsProblem, Is.Null, "Only the variable for this runner's own editor is read.");
        });
    }

    [Test]
    public void SessionOptions_TakeNoArgumentsByDefault()
    {
        var options = SessionProcessOptions.FromEnvironment(static _ => null);

        Assert.Multiple(() =>
        {
            Assert.That(options.EditorArguments, Is.Empty);
            Assert.That(options.EditorArgumentsProblem, Is.Null);
        });
    }

    [Test]
    public void SessionOptions_ReportARefusedFlagOnTheCommandLine()
    {
        var options = SessionProcessOptions.FromEnvironment(
            name => name == "EDITOR_RUNTIME" ? "forge-neo" : null,
            ["--cuda-device", "GPU-00000000-1111-2222-3333-444444444444", "--use-ck-attention"]);

        Assert.That(options.EditorArgumentsProblem, Does.StartWith("--cuda-device chooses the GPU"));
    }

    [Test]
    public void SessionOptions_NameTheVariableWhoseTextCannotBeSplit()
    {
        var options = SessionProcessOptions.FromEnvironment(
            name => name == "RUNNER_EDITOR_ARGS_COMFYUI" ? "--theme 'dark" : null);

        Assert.That(options.EditorArgumentsProblem, Does.StartWith("RUNNER_EDITOR_ARGS_COMFYUI: A single quote"));
    }

    [Test]
    public void AgentOptions_PassTheCommandLineToTheSession()
    {
        var options = RunnerAgentOptions.FromEnvironment(static _ => null, static _ => false, ["--lowvram"]);

        Assert.That(options.Session.EditorArguments, Is.EqualTo(new[] { "--lowvram" }));
    }
}
