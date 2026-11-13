using System.Runtime.Versioning;
using Microsoft.Win32;
using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Supervisor.Tests;

/// <summary>Uses a scratch key under HKCU, never the real Run key.</summary>
[SupportedOSPlatform("windows")]
public class WindowsStartupRegistrationTests
{
    private const string TestRoot = @"Software\OffloadrRunnerTests";

    private string _parentKey = string.Empty;
    private string _runKey = string.Empty;

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("The startup registration exists only on Windows.");
        }

        _parentKey = TestRoot + @"\" + Guid.NewGuid().ToString("n");
        _runKey = _parentKey + @"\Run";
    }

    [TearDown]
    public void TearDown()
    {
        if (OperatingSystem.IsWindows() && _parentKey.Length > 0)
        {
            Registry.CurrentUser.DeleteSubKeyTree(_parentKey, throwOnMissingSubKey: false);
            using var root = Registry.CurrentUser.OpenSubKey(TestRoot);
            if (root is { SubKeyCount: 0, ValueCount: 0 })
            {
                root.Dispose();
                Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
            }
        }
    }

    [Test]
    public void Enable_RegistersTheExecutableToStartMinimized_AndDisableRemovesIt()
    {
        var registration = new WindowsStartupRegistration(@"C:\Apps\Offloadr Runner\OffloadrRunner.exe", _runKey);
        Assert.That(registration.IsEnabled, Is.False);

        registration.SetEnabled(true);

        Assert.Multiple(() =>
        {
            Assert.That(registration.IsEnabled, Is.True);
            Assert.That(Value(), Is.EqualTo("\"C:\\Apps\\Offloadr Runner\\OffloadrRunner.exe\" --minimized"));
        });

        registration.SetEnabled(false);
        registration.SetEnabled(false);

        Assert.That(registration.IsEnabled, Is.False);
    }

    [Test]
    public void RefreshIfMoved_UpdatesAnExistingRegistrationOnly()
    {
        var registration = new WindowsStartupRegistration(@"D:\New\OffloadrRunner.exe", _runKey);
        Assert.That(registration.RefreshIfMoved(), Is.False, "Nothing is registered, so nothing should be added.");
        Assert.That(registration.IsEnabled, Is.False);

        new WindowsStartupRegistration(@"C:\Old\OffloadrRunner.exe", _runKey).SetEnabled(true);

        Assert.Multiple(() =>
        {
            Assert.That(registration.RefreshIfMoved(), Is.True);
            Assert.That(Value(), Is.EqualTo("\"D:\\New\\OffloadrRunner.exe\" --minimized"));
            Assert.That(registration.RefreshIfMoved(), Is.False);
        });
    }

    [Test]
    public void TurnedOffInWindowsStartupApps_IsNotEnabled_AndTurningItOnHereClearsThat()
    {
        var registration = new WindowsStartupRegistration(@"C:\Apps\OffloadrRunner.exe", _runKey);
        registration.SetEnabled(true);

        // Task Manager -> Startup apps -> Disable: the Run value stays, a disabled mark is added.
        SetApproved([3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.That(registration.IsEnabled, Is.False);

        registration.SetEnabled(true);

        Assert.Multiple(() =>
        {
            Assert.That(registration.IsEnabled, Is.True);
            Assert.That(Approved(), Is.Null);
        });
    }

    [Test]
    public void EnabledInWindowsStartupApps_CountsAsEnabled()
    {
        var registration = new WindowsStartupRegistration(@"C:\Apps\OffloadrRunner.exe", _runKey);
        registration.SetEnabled(true);

        SetApproved([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        Assert.That(registration.IsEnabled, Is.True);
    }

    [Test]
    public void Disable_RemovesTheWindowsMarkToo()
    {
        var registration = new WindowsStartupRegistration(@"C:\Apps\OffloadrRunner.exe", _runKey);
        registration.SetEnabled(true);
        SetApproved([3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        registration.SetEnabled(false);

        Assert.That(Approved(), Is.Null);
    }

    [Test]
    public void LaunchPath_OfAnInstalledCopy_IsTheLauncherThatOutlivesUpdates()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\a\AppData\Local\app.offloadr.runner\Update.exe",
            @"C:\Users\a\AppData\Local\app.offloadr.runner\OffloadrRunner.exe",
        };

        var path = WindowsStartupRegistration.LaunchPath(@"C:\Users\a\AppData\Local\app.offloadr.runner\current\OffloadrRunner.exe", files.Contains);

        Assert.That(path, Is.EqualTo(@"C:\Users\a\AppData\Local\app.offloadr.runner\OffloadrRunner.exe"));
    }

    [TestCase(@"C:\Apps\Offloadr Runner\OffloadrRunner.exe")]
    [TestCase(@"C:\Users\a\AppData\Local\app.offloadr.runner\current\OffloadrRunner.exe")]
    public void LaunchPath_WithoutAnInstaller_IsTheExecutableItself(string processPath)
    {
        // No Update.exe or launcher next to "current": a folder that merely has that name.
        Assert.That(WindowsStartupRegistration.LaunchPath(processPath, _ => false), Is.EqualTo(processPath));
    }

    [Test]
    public void RefreshIfMoved_FromAnUnzippedCopy_PointsTheEntryAtTheInstalledLauncher()
    {
        new WindowsStartupRegistration(@"C:\Downloads\OffloadrRunner\OffloadrRunner.exe", _runKey).SetEnabled(true);

        var changed = new WindowsStartupRegistration(@"C:\Local\app.offloadr.runner\OffloadrRunner.exe", _runKey).RefreshIfMoved();

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(Value(), Is.EqualTo("\"C:\\Local\\app.offloadr.runner\\OffloadrRunner.exe\" --minimized"));
        });
    }

    private void SetApproved(byte[] state)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey + "Approved", writable: true);
        key.SetValue(WindowsStartupRegistration.DefaultValueName, state, RegistryValueKind.Binary);
    }

    private byte[]? Approved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey + "Approved");
        return key?.GetValue(WindowsStartupRegistration.DefaultValueName) as byte[];
    }

    private string? Value()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue(WindowsStartupRegistration.DefaultValueName) as string;
    }
}
