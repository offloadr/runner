using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>
/// Starts the app minimized when the person signs in to Windows, through their own (HKCU) Run key.
/// The registration stores this executable's path, so it is refreshed when the app has moved.
/// Windows' own Startup apps switch (Task Manager, Settings) leaves the Run value in place and marks
/// it disabled under StartupApproved instead, so that mark is read and cleared too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string DefaultValueName = "OffloadrRunner";
    public const string MinimizedArgument = "--minimized";

    private readonly string _runKey;
    private readonly string _approvedKey;
    private readonly string _valueName;
    private readonly string _command;

    /// <param name="approvedKey">
    /// Where Windows marks a Run entry disabled. Null: the real one for the real Run key, and a key
    /// next to any other (a test's scratch key), so a test never touches the person's own entries.
    /// </param>
    public WindowsStartupRegistration(string executablePath, string runKey = DefaultRunKey, string valueName = DefaultValueName, string? approvedKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _runKey = runKey;
        _approvedKey = approvedKey ?? (runKey == DefaultRunKey ? DefaultApprovedKey : runKey + "Approved");
        _valueName = valueName;
        _command = $"\"{executablePath}\" {MinimizedArgument}";
    }

    /// <summary>
    /// True when Windows starts this app, at any path, at sign-in: it is registered and not turned off
    /// in Windows' Startup apps.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey);
            return key?.GetValue(_valueName) is string && !IsDisabledByWindows();
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
            key.SetValue(_valueName, _command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
        }

        // Turned on here, it is on: an earlier "disabled" from Windows' Startup apps goes. Turned
        // off, the entry goes, and its mark with it.
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
        approved?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Windows' Startup apps switch writes a binary value whose first byte is odd (3) when the entry
    /// is turned off and even (2) when it is on; no value means on.
    /// </summary>
    private bool IsDisabledByWindows()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey);
        return approved?.GetValue(_valueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
    }

    /// <summary>Points an existing registration at this executable, for example after an update moved the app.</summary>
    /// <returns>True when the registration was changed.</returns>
    public bool RefreshIfMoved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
        if (key?.GetValue(_valueName) is not string current || string.Equals(current, _command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        key.SetValue(_valueName, _command, RegistryValueKind.String);
        return true;
    }
}
