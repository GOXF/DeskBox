using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>Result of <see cref="DesktopListViewProbeNativeMethods.HitTest"/>.</summary>
internal readonly record struct DesktopListViewHit(bool Succeeded, int ItemIndex, string? ItemText, string? Failure)
{
    internal static DesktopListViewHit Fail(string failure) => new(false, -1, null, failure);
}

/// <summary>
/// GOXF fork, 「拖到桌面 = 放回桌面」: a three-state hit test of the desktop
/// list view (blank / which icon / could not tell). The upstream
/// <c>DesktopBlankHitTest</c> folds "icon" and "hit test failed" into one
/// <c>false</c>; this probe keeps them apart and also reads the label of the
/// icon that was hit (LVM_GETITEMTEXTW), so a drop on the dragged entry's own
/// (visible-hidden) icon is not mistaken for a drop on a folder. Same
/// cross-process technique as upstream: LVM_* messages carry process-local
/// pointers, so the structures live in memory allocated inside Explorer.
/// </summary>
internal static class DesktopListViewProbeNativeMethods
{
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint ListViewFirst = 0x1000;
    private const uint ListViewHitTestMessage = ListViewFirst + 18;
    private const uint ListViewGetItemTextW = ListViewFirst + 115;
    private const uint ListViewItemFlagText = 0x0001;
    private const uint SendMessageAbortIfHung = 0x0002;
    private const uint TimeoutMilliseconds = 150;
    private const int TextCapacity = 260;

    internal static DesktopListViewHit HitTest(IntPtr listView, Win32Helper.POINT screenPoint)
    {
        if (listView == IntPtr.Zero)
        {
            return DesktopListViewHit.Fail("no-list-view");
        }

        Win32Helper.POINT clientPoint = screenPoint;
        if (!Win32Helper.ScreenToClient(listView, ref clientPoint))
        {
            return DesktopListViewHit.Fail("screen-to-client");
        }

        Win32Helper.GetWindowThreadProcessId(listView, out uint processId);
        if (processId == 0)
        {
            return DesktopListViewHit.Fail("no-process");
        }

        IntPtr process = RemoteProcessMemoryNativeMethods.OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite,
            false,
            processId);
        if (process == IntPtr.Zero)
        {
            return DesktopListViewHit.Fail($"open-process err={Marshal.GetLastPInvokeError()}");
        }

        int hitSize = Marshal.SizeOf<ListViewHitTestInfo>();
        int itemSize = Marshal.SizeOf<ListViewItem>();
        int textBytes = TextCapacity * sizeof(char);
        int totalSize = hitSize + itemSize + textBytes;
        IntPtr local = IntPtr.Zero;
        IntPtr remote = IntPtr.Zero;
        try
        {
            local = Marshal.AllocHGlobal(totalSize);
            remote = RemoteProcessMemoryNativeMethods.VirtualAllocEx(
                process,
                IntPtr.Zero,
                (UIntPtr)totalSize,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                return DesktopListViewHit.Fail($"alloc err={Marshal.GetLastPInvokeError()}");
            }

            var hit = new ListViewHitTestInfo
            {
                Point = clientPoint,
                ItemIndex = -1,
                SubItemIndex = -1,
                GroupIndex = -1,
            };
            Marshal.StructureToPtr(hit, local, false);
            if (!RemoteProcessMemoryNativeMethods.WriteProcessMemory(process, remote, local, (UIntPtr)hitSize, out _))
            {
                return DesktopListViewHit.Fail("write-hit");
            }

            if (Win32Helper.SendMessageTimeout(
                    listView,
                    ListViewHitTestMessage,
                    UIntPtr.Zero,
                    remote,
                    SendMessageAbortIfHung,
                    TimeoutMilliseconds,
                    out _) == IntPtr.Zero)
            {
                return DesktopListViewHit.Fail($"hit-test-timeout err={Marshal.GetLastPInvokeError()}");
            }

            if (!RemoteProcessMemoryNativeMethods.ReadProcessMemory(process, remote, local, (UIntPtr)hitSize, out _))
            {
                return DesktopListViewHit.Fail("read-hit");
            }

            hit = Marshal.PtrToStructure<ListViewHitTestInfo>(local);
            if (hit.ItemIndex < 0)
            {
                return new DesktopListViewHit(true, -1, null, null);
            }

            return new DesktopListViewHit(true, hit.ItemIndex, ReadItemText(process, listView, local, remote, hit.ItemIndex, hitSize, itemSize), null);
        }
        finally
        {
            if (remote != IntPtr.Zero)
            {
                RemoteProcessMemoryNativeMethods.VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
            }

            if (local != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(local);
            }

            RemoteProcessMemoryNativeMethods.CloseHandle(process);
        }
    }

    /// <summary>Best effort: null when the label cannot be read (the caller then treats the icon as another entry).</summary>
    private static string? ReadItemText(
        IntPtr process,
        IntPtr listView,
        IntPtr local,
        IntPtr remote,
        int index,
        int hitSize,
        int itemSize)
    {
        IntPtr localItem = IntPtr.Add(local, hitSize);
        IntPtr remoteItem = IntPtr.Add(remote, hitSize);
        IntPtr localText = IntPtr.Add(localItem, itemSize);
        IntPtr remoteText = IntPtr.Add(remoteItem, itemSize);
        var item = new ListViewItem
        {
            Mask = ListViewItemFlagText,
            Item = index,
            Text = remoteText,
            TextMax = TextCapacity,
        };
        Marshal.StructureToPtr(item, localItem, false);
        if (!RemoteProcessMemoryNativeMethods.WriteProcessMemory(process, remoteItem, localItem, (UIntPtr)itemSize, out _))
        {
            return null;
        }

        if (Win32Helper.SendMessageTimeout(
                listView,
                ListViewGetItemTextW,
                (UIntPtr)(uint)index,
                remoteItem,
                SendMessageAbortIfHung,
                TimeoutMilliseconds,
                out UIntPtr length) == IntPtr.Zero)
        {
            return null;
        }

        int chars = (int)Math.Min((ulong)length, (ulong)(TextCapacity - 1));
        if (chars <= 0 ||
            !RemoteProcessMemoryNativeMethods.ReadProcessMemory(process, remoteText, localText, (UIntPtr)(chars * sizeof(char)), out _))
        {
            return null;
        }

        return Marshal.PtrToStringUni(localText, chars);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListViewHitTestInfo
    {
        public Win32Helper.POINT Point;
        public uint Flags;
        public int ItemIndex;
        public int SubItemIndex;
        public int GroupIndex;
    }

    /// <summary>LVITEMW (only the fields up to iGroup; Explorer reads mask / iSubItem / pszText / cchTextMax).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ListViewItem
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr Param;
        public int Indent;
        public int GroupId;
        public uint Columns;
        public IntPtr ColumnsPointer;
        public IntPtr ColumnFormats;
        public int Group;
    }
}
