using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Settings;

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>What a diagnostics report describes; gathered by <see cref="DiagnosticsExport"/>.</summary>
public sealed record DiagnosticsInput(
    string AppVersion,
    string OperatingSystem,
    SupervisorView View,
    RunnerSettings Settings,
    IReadOnlyList<string> LogLines,
    IReadOnlyList<DiagnosticLogFile> DiagnosticLogs,
    DateTimeOffset CreatedUtc)
{
    /// <summary>Keys the runner may hold besides the one in Settings (the running container's), removed wherever they appear.</summary>
    public IReadOnlyList<string> OtherRunnerKeys { get; init; } = [];

    /// <summary>The Windows user folder and name, replaced wherever they appear in a path.</summary>
    public string? UserProfile { get; init; }

    public string? UserName { get; init; }
}

/// <summary>One of the app's metadata-only diagnostic log files, or why it could not be read.</summary>
public sealed record DiagnosticLogFile(string Name, string? Text, string? Error = null);

/// <summary>
/// A plain-text report to send to support: what the app and the runner were doing, the checks it ran,
/// its settings and recent logs. Readable before it is sent, so the person can see what it holds. It
/// never holds the runner key (in Settings or in the running container), the key ring, settings.json,
/// bearer tokens or the query of any link (signed links carry their signature there), and the Windows
/// user folder and name are replaced in paths.
/// </summary>
public static partial class DiagnosticsReport
{
    public const string RemovedKey = "[runner key]";
    public const string RemovedQuery = "?[removed]";
    public const string RemovedBearer = "Bearer [removed]";
    public const string UserFolder = "%USERPROFILE%";
    public const string UserName = "[user]";

    /// <summary>The longest output of the in-container GPU check kept; the start and the end matter most.</summary>
    internal const int MaximumCheckOutput = 8 * 1024;

    public static string FileName(DateTimeOffset createdUtc)
        => $"offloadr-runner-diagnostics-{createdUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt";

    public static string Build(DiagnosticsInput input)
    {
        var view = input.View;
        var settings = input.Settings;
        var text = new StringBuilder();
        text.AppendLine("Offloadr Runner diagnostics");
        text.AppendLine(CultureInfo.InvariantCulture, $"Created {input.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        text.AppendLine();
        text.AppendLine("This report holds no runner key, key ring, bearer token or link query, and your Windows user");
        text.AppendLine("folder and name are replaced in paths. It does include the recent runner log from the Logs page,");
        text.AppendLine("which can name your models and files: read it before you send it.");

        Section(text, "App");
        Line(text, "Version", input.AppVersion);
        Line(text, "Operating system", input.OperatingSystem);
        Line(text, ".NET", Environment.Version.ToString());

        Section(text, "Runner");
        Line(text, "State", $"{view.StateTitle} ({view.State})");
        Line(text, "Message", view.Message);
        Line(text, "Restarts", view.Restarts.ToString(CultureInfo.InvariantCulture));
        Line(text, "Updated", Time(view.UpdatedUtc));
        if (view.Fault is { } fault)
        {
            Line(text, "Problem", $"{fault.Kind}: {fault.Title}");
            Line(text, "Problem message", fault.Message);
            Block(text, "Problem detail", fault.Detail);
        }

        if (view.PullProgress is { } pull)
        {
            Line(text, "Image download", pull.Summary);
        }

        if (view.ActiveTemplate is { } template)
        {
            Line(text, "Editor", $"{template.Label} ({template.EditorTemplate}, {template.RuntimeKind})");
            Line(text, "Image", template.Image);
        }

        if (view.Image is { } image)
        {
            Line(text, "Image ID", image.Id);
            Line(text, "Image digest", image.RepoDigest);
        }

        if (view.RunnerStatus is { } status)
        {
            Line(text, "Runner ID", status.RunnerId);
            Line(text, "Runner instance", status.InstanceId);
            Line(text, "Agent version", status.Version);
            Line(text, "Editors it supports", string.Join(", ", status.SupportedEditorTemplates));
            Line(text, "Session running", string.IsNullOrEmpty(status.ActiveSessionId) ? "no" : "yes");
            Line(text, "Status written", Time(status.UpdatedUtc));
        }

        Section(text, "Docker");
        if (view.Engine is { } engine)
        {
            Line(text, "Available", YesNo(engine.Available));
            Line(text, "Engine", engine.Kind.ToString());
            Line(text, "Executable", engine.Executable);
            Line(text, "Server version", engine.ServerVersion);
            Line(text, "Server OS", engine.ServerOs);
            Line(text, "Operating system", engine.OperatingSystem);
            Line(text, "NVIDIA runtime listed", YesNo(engine.NvidiaRuntimeListed));
            Block(text, "Error", engine.Error);
        }
        else
        {
            text.AppendLine("Not checked yet.");
        }

        Section(text, "GPU");
        if (view.HostGpus is { } gpus)
        {
            foreach (var gpu in gpus.Gpus)
            {
                Line(text, "GPU", $"{gpu.Name}, {gpu.MemoryMiB.ToString(CultureInfo.InvariantCulture)} MiB, {gpu.Uuid ?? "no UUID"}");
            }

            if (gpus.Gpus.Count == 0)
            {
                text.AppendLine("No NVIDIA GPU found on this computer.");
            }

            Block(text, "Error", gpus.Error);
        }
        else
        {
            text.AppendLine("Not checked yet.");
        }

        if (view.GpuAccess is { } access)
        {
            Line(text, "GPU inside a container", access.Succeeded ? "works" : "failed");
            // Redacted whole before it is cut: a key or link split by the cut could no longer be recognised.
            Block(text, "Check output", Shorten(Redact(access.Output, input), MaximumCheckOutput));
        }

        if (view.Plan is { } plan)
        {
            Section(text, "Launch plan");
            Line(text, "From", plan.Source.ToString());
            Line(text, "Selected editor", plan.SelectedEditorTemplate);
            Line(text, "Notice", plan.Notice);
            foreach (var offered in plan.Templates)
            {
                Line(text, "Editor", $"{offered.Label} ({offered.EditorTemplate}, {offered.RuntimeKind}): {offered.Image}");
            }
        }

        Section(text, "Settings");
        Line(text, "Offloadr address", settings.OffloadrApiOrigin);
        Line(text, "Runner key", settings.HasRunnerKey ? "set" : "not set");
        Line(text, "Models folder", string.IsNullOrWhiteSpace(settings.LocalModelsDirectory) ? "none" : settings.LocalModelsDirectory);
        Line(text, "Download models", YesNo(settings.DownloadModels));
        Line(text, "Docker executable", string.IsNullOrWhiteSpace(settings.ContainerEngineExecutable) ? "found automatically" : settings.ContainerEngineExecutable);
        Line(text, "Editor in Settings", settings.SelectedEditorTemplate);
        Line(text, "Make this GPU available when the app opens", YesNo(settings.AutoStart));
        Line(text, "Keep awake during sessions", YesNo(settings.KeepAwakeDuringSessions));
        Line(text, "GPU in Settings", string.IsNullOrWhiteSpace(settings.GpuUuid) ? "all" : settings.GpuUuid.Trim());
        Line(text, "Extra ComfyUI arguments", Flags(settings.ComfyUiArguments));
        Line(text, "Extra Forge Neo arguments", Flags(settings.ForgeNeoArguments));
        var problems = settings.Validate(_ => true);
        foreach (var problem in problems)
        {
            Line(text, "Problem", problem);
        }

        Section(text, $"Runner log ({input.LogLines.Count.ToString(CultureInfo.InvariantCulture)} recent lines)");
        foreach (var line in input.LogLines)
        {
            text.AppendLine(line);
        }

        Section(text, "Diagnostic log (metadata only)");
        if (input.DiagnosticLogs.Count == 0)
        {
            text.AppendLine("None.");
        }

        foreach (var file in input.DiagnosticLogs)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"-- {file.Name}");
            if (file.Error is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Could not be read: {file.Error}");
            }
            else
            {
                text.Append(file.Text);
                if (file.Text is { Length: > 0 } read && read[^1] != '\n')
                {
                    text.AppendLine();
                }
            }
        }

        return Redact(text.ToString(), input);
    }

    /// <summary>Removes what the report must never hold, wherever it appears: the last step before the report leaves the app.</summary>
    public static string Redact(string text, DiagnosticsInput input)
    {
        // Link queries and bearer tokens first, while the text is as written: the placeholders below hold
        // spaces, which would end a link or token early and leave the rest of it behind.
        text = LinkQuery().Replace(text, match => match.Groups["link"].Value + RemovedQuery);
        text = BearerToken().Replace(text, RemovedBearer);

        // Longest first, so a key that contains another is removed whole.
        var keys = input.OtherRunnerKeys.Append(input.Settings.RunnerKey)
            .Select(key => key?.Trim())
            .Where(key => key is { Length: >= 8 })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(key => key!.Length);
        foreach (var key in keys)
        {
            text = text.Replace(key!, RemovedKey, StringComparison.Ordinal);
        }

        if (input.UserProfile?.TrimEnd('\\', '/') is { Length: > 3 } profile)
        {
            // With either slash, and only as a whole folder: /root is not /rootfs.
            var profilePath = new Regex(
                @"(?<![\w.-])" + string.Join(@"[\\/]", profile.Split('\\', '/').Select(Regex.Escape)) + PathEnd,
                PathOptions,
                TimeSpan.FromSeconds(2));
            text = profilePath.Replace(text, UserFolder);
        }

        if (input.UserName is { Length: > 0 } name)
        {
            // The user folder as Docker and WSL write it (/mnt/c/Users/name, /run/desktop/mnt/host/c/Users/name),
            // and a Linux home.
            var userPath = new Regex(@"(?<prefix>[\\/](?:Users|home)[\\/])" + Regex.Escape(name) + PathEnd, PathOptions, TimeSpan.FromSeconds(2));
            text = userPath.Replace(text, match => match.Groups["prefix"].Value + UserName);
        }

        return text;
    }

    private const string PathEnd = @"(?=$|[\\/\s'"":;,)\]])";

    private const RegexOptions PathOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline;

    [GeneratedRegex(@"\bBearer\s+[^\s""',;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    // An apostrophe may be part of a link (alex's-model), so it does not end one; a link in single quotes
    // then loses its closing quote with its query, which only removes more.
    [GeneratedRegex(@"(?<link>\b[a-z][a-z0-9+.-]*://[^\s?#""<>]+)\?[^\s""<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkQuery();

    private static void Section(StringBuilder text, string title)
    {
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"== {title}");
    }

    private static void Line(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {value.ReplaceLineEndings(" ")}");
        }
    }

    private static void Block(StringBuilder text, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"{label}:");
        foreach (var line in value.TrimEnd().ReplaceLineEndings("\n").Split('\n'))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    {line}");
        }
    }

    private static string Shorten(string text, int maximum)
        => text.Length <= maximum
            ? text
            : string.Concat(text.AsSpan(0, maximum / 2), "\n[…]\n", text.AsSpan(text.Length - maximum / 2));

    private static string YesNo(bool value) => value ? "yes" : "no";

    // Flags only, as the agent logs them: a value can hold anything.
    private static string Flags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "none";
        }

        return EditorArguments.TrySplit(text, out var arguments, out _)
            ? EditorArguments.Describe(arguments)
            : "set, but cannot be read";
    }

    private static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}
