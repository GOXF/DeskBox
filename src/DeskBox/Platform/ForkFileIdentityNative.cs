using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

/// <summary>
/// GOXF fork, 「映射整理」: stable NTFS identity (volume serial + file index)
/// of a desktop entry DeskBox hid, so an entry that leaves the desktop (moved
/// to another folder, cut/paste, dragged out, recycled) can be found again by
/// its id and its hidden attribute cleared at the new location. A
/// FileSystemWatcher only reports such a move as a deletion on the desktop.
/// </summary>
internal static partial class ForkFileIdentityNative
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public int Type; // FileIdType = 0
        [FieldOffset(8)] public long FileId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint, in FileIdDescriptor fileId, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint flagsAndAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    /// <summary>Identity of a file or folder, or null when it cannot be read (not NTFS, gone, no access).</summary>
    public static (uint VolumeSerial, ulong FileIndex)? TryGet(string path)
    {
        try
        {
            using SafeFileHandle handle = CreateFile(path, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            {
                return null;
            }

            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return index == 0 ? null : (info.VolumeSerialNumber, index);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Final path (\\?\ form) of the entry with this id, opened by id on the
    /// volume of <paramref name="volumeHintDirectory"/>; null when the entry
    /// no longer exists there (deleted, or moved to another volume).
    /// </summary>
    public static unsafe string? TryResolvePath(uint volumeSerial, ulong fileIndex, string volumeHintDirectory)
    {
        try
        {
            using SafeFileHandle hint = CreateFile(volumeHintDirectory, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
            if (hint.IsInvalid ||
                !GetFileInformationByHandle(hint, out ByHandleFileInformation hintInfo) ||
                hintInfo.VolumeSerialNumber != volumeSerial)
            {
                return null;
            }

            var descriptor = new FileIdDescriptor { Size = 24, Type = 0, FileId = unchecked((long)fileIndex) };
            using SafeFileHandle file = OpenFileById(hint, in descriptor, FileReadAttributes, FileShareAll, 0, FileFlagBackupSemantics);
            if (file.IsInvalid)
            {
                return null;
            }

            char* buffer = stackalloc char[1024];
            uint length = GetFinalPathNameByHandle(file, buffer, 1024, 0);
            if (length == 0 || length >= 1024)
            {
                return null;
            }

            return new string(buffer, 0, (int)length);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
