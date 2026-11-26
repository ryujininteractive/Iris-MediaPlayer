using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// Windows' own Open dialog, asked for a plain path. WinRT's FileOpenPicker shows the same dialog but
/// then hands the choice over as a StorageFile through a broker, and that hand-off fails for some
/// folders (E_FAIL) even though the file opens fine. Iris only needs the path, so it skips the broker.
/// </summary>
internal static class FileDialogs
{
    private const uint PickFolders = 0x20;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint FileMustExist = 0x1000;
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>The chosen file's path, or null when the dialog was closed.</summary>
    /// <param name="filterName">Shown in the file type box, for example "Videos".</param>
    /// <param name="extensions">Extensions with the dot, for example ".mkv".</param>
    public static string? PickFile(IntPtr owner, string filterName, IEnumerable<string> extensions)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogClass();
        try
        {
            var pattern = string.Join(";", extensions.Select(extension => "*" + extension));
            dialog.SetFileTypes(2, [new FilterSpec(filterName, pattern), new FilterSpec("All files", "*.*")]);
            dialog.SetOptions(ForceFileSystem | PathMustExist | FileMustExist);
            return Show(dialog, owner);
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    /// <summary>The chosen folder's path, or null when the dialog was closed.</summary>
    public static string? PickFolder(IntPtr owner)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogClass();
        try
        {
            dialog.SetOptions(PickFolders | ForceFileSystem | PathMustExist);
            return Show(dialog, owner);
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    private static string? Show(IFileOpenDialog dialog, IntPtr owner)
    {
        // Starts in Videos the first time; afterwards Windows remembers the last folder.
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (videos.Length > 0 && SHCreateItemFromParsingName(videos, IntPtr.Zero, typeof(IShellItem).GUID, out var start) == 0)
        {
            dialog.SetDefaultFolder(start);
            Marshal.ReleaseComObject(start);
        }

        var result = dialog.Show(owner);
        if (result == Cancelled)
            return null;
        Marshal.ThrowExceptionForHR(result);

        dialog.GetResult(out var item);
        try
        {
            item.GetDisplayName(FileSystemPath, out var pathPointer);
            try
            {
                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private const uint FileSystemPath = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid riid, out IShellItem item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct FilterSpec(string name, string spec)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Spec = spec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass;

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr owner);

        // IFileDialog
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);

        // IFileOpenDialog
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, in Guid handler, in Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
