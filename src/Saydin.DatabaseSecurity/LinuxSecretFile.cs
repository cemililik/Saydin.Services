using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Saydin.DatabaseSecurity;

internal static class LinuxSecretFile
{
    private const int ReadOnly = 0;
    private const int CloseOnExec = 0x80000;
    private const int NoFollow = 0x20000;
    private const int CurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7ff;
    private const uint StatxMountId = 0x1000;
    private const uint RequestedMask = StatxBasicStats | StatxMountId;
    private const uint RequiredMask = 0x3cf | StatxMountId;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;
    private const uint DirectoryFile = 0x4000;
    private const uint PermissionMask = 0x1FF;
    private const uint FileMode0400 = 0x100;
    private const uint FileMode0600 = 0x180;
    private const uint DirectoryMode0700 = 0x1C0;
    private const long OpenAt2SystemCall = 437;
    private const ulong ResolveNoSymlinks = 0x04;

    internal static Action? AfterOpenBeforeReadForTests { get; set; }
    internal static Func<SecretFileObservationStage, ulong, ulong>? MountIdForTests { get; set; }
    internal static uint RequestedMaskForTests => RequestedMask;
    internal static uint RequiredMaskForTests => RequiredMask;
    internal static int OpenHowSizeForTests => Marshal.SizeOf<OpenHow>();
    internal static int FileStatusSizeForTests => Marshal.SizeOf<FileStatus>();
    internal static int MountIdOffsetForTests => checked((int)Marshal.OffsetOf<FileStatus>(nameof(FileStatus.MountId)));

    public static byte[] Read(string path, int minimumBytes, int maximumBytes, string rejectionCode)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || Statx(CurrentWorkingDirectory, parent, AtSymlinkNoFollow,
                RequestedMask, out var parentStat) != 0 ||
            (parentStat.Mode & FileTypeMask) != DirectoryFile ||
            (parentStat.Mode & PermissionMask) != DirectoryMode0700 ||
            parentStat.UserId != GetEffectiveUserId() || !HasRequiredFields(parentStat))
            throw Rejected(rejectionCode);
        ObserveMount(ref parentStat, SecretFileObservationStage.ParentBefore);

        if (Statx(CurrentWorkingDirectory, path, AtSymlinkNoFollow, RequestedMask, out var before) != 0 ||
            (before.Mode & FileTypeMask) != RegularFile ||
            !IsAcceptedSecretMode(before.Mode) ||
            before.UserId != GetEffectiveUserId() || before.HardLinks != 1 ||
            !HasRequiredFields(before) || before.Size < (ulong)minimumBytes ||
            before.Size > (ulong)maximumBytes)
            throw Rejected(rejectionCode);
        ObserveMount(ref before, SecretFileObservationStage.PathBefore);

        var openHow = new OpenHow { Flags = ReadOnly | CloseOnExec | NoFollow, Resolve = ResolveNoSymlinks };
        var descriptorResult = OpenAt2(
            OpenAt2SystemCall, CurrentWorkingDirectory, path, ref openHow, (nuint)24);
        if (descriptorResult < 0 || descriptorResult > int.MaxValue) throw Rejected(rejectionCode);
        using var handle = new SafeFileHandle((IntPtr)(int)descriptorResult, ownsHandle: true);
        if (Statx((int)descriptorResult, string.Empty, AtEmptyPath, RequestedMask, out var opened) != 0)
            throw Rejected(rejectionCode);
        ObserveMount(ref opened, SecretFileObservationStage.OpenedHandle);
        if (
            !SameIdentity(before, opened) || !IsAcceptedSecretMode(opened.Mode) ||
            opened.UserId != GetEffectiveUserId() || opened.HardLinks != 1 || !HasRequiredFields(opened))
            throw Rejected(rejectionCode);

        var bytes = new byte[checked((int)opened.Size)];
        AfterOpenBeforeReadForTests?.Invoke();
        using (var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false))
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw Rejected(rejectionCode);
            if (Statx(CurrentWorkingDirectory, path, AtSymlinkNoFollow, RequestedMask, out var afterPath) != 0 ||
                Statx((int)descriptorResult, string.Empty, AtEmptyPath, RequestedMask, out var afterHandle) != 0 ||
                Statx(CurrentWorkingDirectory, parent, AtSymlinkNoFollow, RequestedMask, out var afterParent) != 0)
                throw Rejected(rejectionCode);
            ObserveMount(ref afterPath, SecretFileObservationStage.PathAfter);
            ObserveMount(ref afterHandle, SecretFileObservationStage.HandleAfter);
            ObserveMount(ref afterParent, SecretFileObservationStage.ParentAfter);
            if (
                !SameIdentity(parentStat, afterParent) || !SameIdentity(before, afterPath) ||
                !SameIdentity(before, afterHandle) || afterHandle.Size != (ulong)bytes.Length ||
                !IsAcceptedSecretMode(afterHandle.Mode) ||
                afterHandle.UserId != GetEffectiveUserId() || afterHandle.HardLinks != 1 ||
                !HasRequiredFields(afterPath) || !HasRequiredFields(afterHandle) ||
                !HasRequiredFields(afterParent))
                throw Rejected(rejectionCode);
        }
        return bytes;
    }

    internal static bool MountIdentityMatchesForTests(ulong left, ulong right) => left == right;

    private static bool IsAcceptedSecretMode(uint mode) =>
        (mode & PermissionMask) is FileMode0400 or FileMode0600;

    private static void ObserveMount(ref FileStatus status, SecretFileObservationStage stage)
    {
        if (MountIdForTests is { } transform) status.MountId = transform(stage, status.MountId);
    }

    private static bool SameIdentity(FileStatus left, FileStatus right) =>
        left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor &&
        MountIdentityMatchesForTests(left.MountId, right.MountId) && left.Inode == right.Inode &&
        left.Size == right.Size && left.Mode == right.Mode && left.UserId == right.UserId &&
        left.HardLinks == right.HardLinks &&
        left.Change.Seconds == right.Change.Seconds && left.Change.Nanoseconds == right.Change.Nanoseconds &&
        left.Modification.Seconds == right.Modification.Seconds &&
        left.Modification.Nanoseconds == right.Modification.Nanoseconds;

    private static bool HasRequiredFields(FileStatus status) => (status.Mask & RequiredMask) == RequiredMask;

    private static DatabaseSecurityRejectedException Rejected(string code) =>
        new(code, DatabaseSecurityFailureKind.SecretRejected);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        private readonly int reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct FileStatus
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint HardLinks;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        private readonly ushort padding;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        private readonly StatxTimestamp access;
        private readonly StatxTimestamp birth;
        public StatxTimestamp Change;
        public StatxTimestamp Modification;
        private readonly uint rawDeviceMajor;
        private readonly uint rawDeviceMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true,
        CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern long OpenAt2(long number, int directoryDescriptor, string path, ref OpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true,
        CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Statx(int directoryDescriptor, string path, int flags, uint mask, out FileStatus status);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}

internal enum SecretFileObservationStage
{
    ParentBefore,
    PathBefore,
    OpenedHandle,
    PathAfter,
    HandleAfter,
    ParentAfter,
}
