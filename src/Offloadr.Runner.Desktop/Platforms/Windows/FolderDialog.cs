using System.Runtime.InteropServices;

namespace Offloadr.Runner.Desktop;

/// <summary>
/// The Windows folder dialog (IFileOpenDialog). WinRT's FolderPicker, like this dialog when told to
/// return file system folders only, leaves out shell locations such as Linux (the WSL distributions),
/// where a models folder often lives. So the dialog shows everything Explorer shows, and only refuses
/// a choice that has no file system path (Linux or This PC itself): it then says so and stays open.
/// </summary>
internal static class FolderDialog
{
    private const uint PickFolders = 0x20;
    private const uint PathMustExist = 0x800;
    private const uint FileSystemPath = 0x80058000;
    private const int Ok = 0;
    private const int False = 1;
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint IconInformation = 0x40;

    /// <summary>Shows the dialog over <paramref name="owner"/>; null when it is cancelled.</summary>
    public static string? Show(IntPtr owner)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        var events = new Events(dialog);
        uint cookie = 0;
        var advised = false;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | PickFolders | PathMustExist);
            dialog.Advise(events, out cookie);
            advised = true;
            var result = dialog.Show(owner);
            if (result == Cancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(result);
            return events.Path;
        }
        finally
        {
            if (advised)
            {
                dialog.Unadvise(cookie);
            }

            Marshal.FinalReleaseComObject(dialog);
        }
    }

    /// <summary>The file system path of the dialog's current choice, or null when it has none.</summary>
    private static string? ResultPath(IFileOpenDialog dialog)
    {
        IShellItem? item = null;
        try
        {
            if (dialog.GetResult(out item) != Ok || item is null || item.GetDisplayName(FileSystemPath, out var name) != Ok)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        finally
        {
            if (item is not null)
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
    }

    /// <summary>Keeps the dialog open for a choice without a file system path, and keeps the path of one with.</summary>
    private sealed class Events(IFileOpenDialog dialog) : IFileDialogEvents
    {
        public string? Path { get; private set; }

        public int OnFileOk(IntPtr dialogPointer)
        {
            Path = ResultPath(dialog);
            if (Path is not null)
            {
                return Ok;
            }

            // Over the dialog, which stays open: without a word, the button would seem not to work.
            ((IOleWindow)dialog).GetWindow(out var window);
            _ = MessageBox(
                window,
                "This location is not a folder on a drive. Open it and choose a folder inside it, such as one in a drive or a Linux distribution.",
                "Choose a folder",
                IconInformation);
            return False;
        }

        public int OnFolderChanging(IntPtr dialogPointer, IntPtr folder) => Ok;

        public int OnFolderChange(IntPtr dialogPointer) => Ok;

        public int OnSelectionChange(IntPtr dialogPointer) => Ok;

        public int OnShareViolation(IntPtr dialogPointer, IntPtr item, out int response)
        {
            response = 0;
            return Ok;
        }

        public int OnTypeChange(IntPtr dialogPointer) => Ok;

        public int OnOverwrite(IntPtr dialogPointer, IntPtr item, out int response)
        {
            response = 0;
            return Ok;
        }
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog;

    // Methods in vtable order: IModalWindow, IFileDialog, then IFileOpenDialog. Only the ones used
    // here have real signatures.
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr owner);

        void SetFileTypes();

        void SetFileTypeIndex();

        void GetFileTypeIndex();

        void Advise(IFileDialogEvents events, out uint cookie);

        void Unadvise(uint cookie);

        void SetOptions(uint options);

        void GetOptions(out uint options);

        void SetDefaultFolder();

        void SetFolder();

        void GetFolder();

        void GetCurrentSelection();

        void SetFileName();

        void GetFileName();

        void SetTitle();

        void SetOkButtonLabel();

        void SetFileNameLabel();

        [PreserveSig]
        int GetResult(out IShellItem item);
    }

    [ComImport]
    [Guid("973510DB-7D7F-452B-8975-74A85828D354")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig]
        int OnFileOk(IntPtr dialog);

        [PreserveSig]
        int OnFolderChanging(IntPtr dialog, IntPtr folder);

        [PreserveSig]
        int OnFolderChange(IntPtr dialog);

        [PreserveSig]
        int OnSelectionChange(IntPtr dialog);

        [PreserveSig]
        int OnShareViolation(IntPtr dialog, IntPtr item, out int response);

        [PreserveSig]
        int OnTypeChange(IntPtr dialog);

        [PreserveSig]
        int OnOverwrite(IntPtr dialog, IntPtr item, out int response);
    }

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        void GetWindow(out IntPtr window);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();

        void GetParent();

        [PreserveSig]
        int GetDisplayName(uint form, out IntPtr name);
    }
}
