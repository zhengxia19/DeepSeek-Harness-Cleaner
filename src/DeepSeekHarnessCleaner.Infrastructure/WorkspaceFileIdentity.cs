using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeepSeekHarnessCleaner.Infrastructure;

/// <summary>Windows local-file identity; hashes alone cannot detect an identical-byte path replacement.</summary>
internal sealed record WorkspaceFileIdentity(uint Volume, uint IndexHigh, uint IndexLow, long CreationTime,
    long WriteTime, long Length, string Sha256)
{
    internal static FileStream OpenForRename(string path) => OpenPinned(path, 0x80010000);
    internal static FileStream OpenPinnedRead(string path) => OpenPinned(path, 0x80000000);
    private static FileStream OpenPinned(string path, uint access)
    {
        var handle = CreateFileW(path, access, FileShare.Read, IntPtr.Zero, FileMode.Open, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("SYNC_RENAME_LEASE_FAILED", new Win32Exception(error));
        }
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Rename the verified handle itself; never follow a substituted source link or overwrite a new target.</summary>
    internal static void RenameNoReplace(FileStream stream, string destination)
    {
        var name = Encoding.Unicode.GetBytes(destination);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var size = nameOffset + name.Length + 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, 0); // ReplaceIfExists = false.
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(stream.SafeFileHandle, 3, buffer, (uint)size))
                throw new IOException("SYNC_HANDLE_RENAME_FAILED", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static WorkspaceFileIdentity Read(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var before)) throw new IOException("SYNC_FILE_ID_UNAVAILABLE", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((before.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || before.Links != 1
            || before.Volume == 0 || (before.IndexHigh == 0 && before.IndexLow == 0))
            throw new IOException("SYNC_FILE_TYPE_OR_LINKS_INVALID");
        stream.Position = 0;
        if (stream.Length > WorkspaceJson.MaxBytes) throw new InvalidDataException("SYNC_SIZE_LIMIT");
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var after) || before.Volume != after.Volume
            || before.IndexHigh != after.IndexHigh || before.IndexLow != after.IndexLow || before.Links != after.Links
            || before.Attributes != after.Attributes || before.WriteHigh != after.WriteHigh || before.WriteLow != after.WriteLow
            || before.SizeHigh != after.SizeHigh || before.SizeLow != after.SizeLow
            || before.CreationHigh != after.CreationHigh || before.CreationLow != after.CreationLow) throw new IOException("SYNC_FILE_CHANGED");
        return new(before.Volume, before.IndexHigh, before.IndexLow, Join(before.CreationHigh, before.CreationLow),
            Join(before.WriteHigh, before.WriteLow), Join(before.SizeHigh, before.SizeLow), hash);
    }

    /// <summary>Keep every existing source ancestor from being renamed/replaced during Apply.</summary>
    internal static IDisposable PinDirectories(string directory)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var handle = CreateFileW(current.FullName, 0x80, FileShare.Read | FileShare.Write, IntPtr.Zero,
                    FileMode.Open, 0x02000000 | 0x00200000, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT; no delete sharing.
                handles.Add(handle);
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                    || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new IOException("SYNC_DIRECTORY_LEASE_FAILED");
            }
            return new DirectoryPins(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    private static long Join(uint high, uint low) => ((long)high << 32) | low;
    private sealed class DirectoryPins(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);
}
