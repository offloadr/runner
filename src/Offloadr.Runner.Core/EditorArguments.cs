using System.Text;

namespace Offloadr.Runner.Core;

/// <summary>
/// Extra command-line arguments the runner's owner adds to the editor, such as <c>--lowvram</c>. They
/// follow the runner's own arguments. Flags the runner sets, and flags that move the editor's files,
/// change where or to whom it listens, choose the GPU or end it, are refused: the runner relies on them,
/// and the editor's parser lets the last of a repeated flag win. Each editor family has its own flags, so
/// the arguments are per runtime kind, and a runtime kind without a reviewed list takes none.
/// </summary>
public static class EditorArguments
{
    /// <summary>The longest argument text accepted, in characters.</summary>
    public const int MaxLength = 4096;

    private const string GpuHint = "to use one GPU, set OFFLOADR_GPU_UUID to its UUID instead.";

    // ComfyUI, Forge Neo and their entrypoints all set these, or the runner reaches the editor through them.
    private static readonly string[] SharedReserved = ["--listen", "--port", "--tls-keyfile", "--tls-certfile"];

    // Both parsers add --help; with it the editor prints its usage and exits.
    private static readonly string[] SharedExit = ["--help"];

    private static readonly Dictionary<string, Family> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comfyui"] = new(
            "ComfyUI",
            Reserved:
            [
                .. SharedReserved,
                "--base-directory", "--input-directory", "--output-directory", "--temp-directory",
                "--user-directory", "--models-directory", "--previews-directory", "--extra-model-paths-config",
                "--disabled-nodes-config", "--database-url", "--multi-user", "--enable-cors-header",
                // The folder the editor serves its web pages from, with every other file in it.
                "--front-end-root",
                "--auto-launch", "--disable-auto-launch", "--windows-standalone-build",
            ],
            Gpu: ["--cuda-device", "--default-device", "--oneapi-device-selector", "--directml"],
            Exit: [.. SharedExit, "--quick-test-for-ci", "--list-feature-flags"]),
        ["forge-neo"] = new(
            "Forge Neo",
            Reserved:
            [
                .. SharedReserved,
                "--api", "--nowebui", "--data-dir", "--model-ref", "--ckpt-dirs", "--lora-dirs", "--vae-dirs",
                "--text-encoder-dirs", "--embeddings-dir", "--localizations-dir", "--codeformer-models-path",
                "--esrgan-models-path", "--gfpgan-models-path", "--forge-ref-a1111-home", "--forge-ref-comfy-home",
                "--forge-ref-comfy-yaml", "--styles-file", "--ui-config-file", "--ui-settings-file",
                "--gradio-allowed-path", "--gradio-auth", "--gradio-auth-path", "--api-auth", "--server-name",
                "--subpath", "--share", "--ngrok", "--ngrok-options", "--ngrok-region", "--cors-allow-origins",
                "--cors-allow-origins-regex", "--autolaunch", "--add-stop-route", "--api-server-stop",
            ],
            // --cuda-device is ComfyUI's, but people bring it over, and Forge Neo would stop on it.
            Gpu: ["--gpu-device-id", "--device-id", "--cuda-device", "--directml"],
            Exit: [.. SharedExit, "--dump-sysinfo"]),
    };

    // A flag a later editor release adds for a file or folder is refused before it is reviewed.
    private static readonly string[] PathSuffixes = ["-dir", "-dirs", "-directory", "-path", "-paths", "-file", "-home"];

    /// <summary>The editor families that take extra arguments, by runtime kind.</summary>
    public static IReadOnlyCollection<string> RuntimeKinds => Families.Keys;

    /// <summary>The variable that holds the arguments for <paramref name="runtimeKind"/>, such as <c>RUNNER_EDITOR_ARGS_FORGE_NEO</c>.</summary>
    public static string EnvironmentVariable(string runtimeKind)
        => "RUNNER_EDITOR_ARGS_" + runtimeKind.Trim().ToUpperInvariant().Replace('-', '_');

    /// <summary>
    /// Splits <paramref name="text"/> the way a shell does: spaces separate arguments, single quotes keep
    /// text as it is, double quotes keep spaces, and a backslash escapes the next character outside single
    /// quotes. Blank text is no arguments.
    /// </summary>
    /// <returns>False with <paramref name="error"/> when the text cannot be split.</returns>
    public static bool TrySplit(string? text, out IReadOnlyList<string> arguments, out string? error)
    {
        arguments = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.Length > MaxLength)
        {
            error = $"The editor arguments are longer than {MaxLength} characters.";
            return false;
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inArgument = false;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0';
                else current.Append(c);
            }
            else if (c == '\\' && i + 1 < text.Length && (quote == '\0' || text[i + 1] is '"' or '\\'))
            {
                current.Append(text[++i]);
                inArgument = true;
            }
            else if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else current.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inArgument = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inArgument)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inArgument = false;
                }
            }
            else
            {
                current.Append(c);
                inArgument = true;
            }
        }

        if (quote != '\0')
        {
            error = $"A {(quote == '"' ? "double" : "single")} quote in the editor arguments is not closed.";
            return false;
        }

        if (inArgument)
        {
            result.Add(current.ToString());
        }

        arguments = result;
        return true;
    }

    /// <summary>Why <paramref name="arguments"/> cannot be given to the editor of <paramref name="runtimeKind"/>, or null when they can.</summary>
    public static string? Problem(string runtimeKind, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return null;
        }

        if (!Families.TryGetValue(runtimeKind.Trim(), out var family))
        {
            return $"The {runtimeKind} editor does not take extra arguments.";
        }

        if (arguments.Sum(argument => argument.Length + 1) > MaxLength)
        {
            return $"The editor arguments are longer than {MaxLength} characters.";
        }

        if (arguments.Any(argument => argument.Any(char.IsControl)))
        {
            return "The editor arguments cannot contain control characters, such as line breaks.";
        }

        if (!arguments[0].StartsWith('-'))
        {
            return $"{family.Name} arguments start with a flag, such as --lowvram; '{arguments[0]}' is not one.";
        }

        foreach (var argument in arguments)
        {
            // The editors' only short options are -h, which prints help and exits, and Forge Neo's -f,
            // which skips a safety check; argparse also reads them inside a group such as -fh. A negative
            // number is a value, not an option.
            if (argument.Length >= 2 && argument[0] == '-' && char.IsAsciiLetter(argument[1]))
            {
                return $"{argument}: short options are not taken; write the long flag, such as --lowvram.";
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length <= 2)
            {
                continue;
            }

            var flag = argument.Split('=', 2)[0];
            if (Match(family.Gpu, flag) is { } gpu)
            {
                return $"{Spelled(flag, gpu)} chooses the GPU; {GpuHint}";
            }

            if (Match(family.Reserved, flag) is { } reserved)
            {
                return $"{Spelled(flag, reserved)} is one the runner sets or depends on, so it cannot be added.";
            }

            if (Match(family.Exit, flag) is { } exit)
            {
                return $"{Spelled(flag, exit)} makes the editor print something and exit, so no session could start.";
            }

            if (PathSuffixes.Any(suffix => flag.EndsWith(suffix, StringComparison.Ordinal)))
            {
                return $"{flag} names a file or folder; the runner decides where the editor keeps its files.";
            }
        }

        return null;
    }

    /// <summary>The flags alone, without their values, for logs: a value can hold anything.</summary>
    public static string Describe(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments
            .Where(argument => argument.StartsWith('-'))
            .Select(argument => argument.Split('=', 2)[0]));

    // The editors' parsers accept any unambiguous abbreviation of a long flag, so a flag that begins a
    // refused one is refused too.
    private static string? Match(string[] refused, string flag)
        => refused.FirstOrDefault(candidate => string.Equals(candidate, flag, StringComparison.Ordinal)) ??
           refused.FirstOrDefault(candidate => candidate.StartsWith(flag, StringComparison.Ordinal));

    private static string Spelled(string flag, string refused)
        => string.Equals(flag, refused, StringComparison.Ordinal) ? flag : $"{flag} (short for {refused})";

    private sealed record Family(string Name, string[] Reserved, string[] Gpu, string[] Exit);
}
