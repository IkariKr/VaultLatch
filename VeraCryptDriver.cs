using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VaultLatch;

internal sealed class VeraCryptDriver
{
    private const string DriverPath = @"\\.\VeraCrypt";
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    // VeraCrypt 1.26.x src/Common/Tcdefs.h + src/Common/Apidrvr.h.
    private const int ExpectedDriverVersion = 0x0126;
    private const int ExpectedMountStructSize = 982;
    private const int MaxPasswordBytes = 128;
    private const int MaxPimValue = 2_147_468;

    // TC_IOCTL(CODE) = CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800 + CODE, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlGetDriverVersion = 0x00222004; // TC_IOCTL(1)
    private const uint IoctlMountVolume = 0x0022200C; // TC_IOCTL(3)
    private const uint IoctlUnmountVolume = 0x00222010; // TC_IOCTL(4)
    private const uint IoctlWipePasswordCache = 0x00222030; // TC_IOCTL(12)

    private const int ErrSuccess = 0;
    private const int ErrorSharingViolation = 32;

    private readonly FileLogger _logger;

    public VeraCryptDriver(FileLogger logger)
    {
        _logger = logger;
    }

    public bool CanOpen()
    {
        try
        {
            using var driver = OpenDriver();
            var version = GetDriverVersion(driver);
            if (version != ExpectedDriverVersion)
            {
                _logger.Error($"VeraCrypt 驱动版本不兼容：0x{version:X4}，VaultLatch 当前支持 0x{ExpectedDriverVersion:X4}。");
                return false;
            }

            ValidateMountStructLayout();
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("VaultLatch 无法直接访问 VeraCrypt 驱动", exception);
            return false;
        }
    }

    public int GetVersion()
    {
        using var driver = OpenDriver();
        return GetDriverVersion(driver);
    }

    public Task<MountResult> MountAsync(string volumePath, string driveLetter, byte[] passwordUtf8, int pim)
    {
        ArgumentNullException.ThrowIfNull(passwordUtf8);
        return Task.Run(() => Mount(volumePath, driveLetter, passwordUtf8, pim));
    }

    public Task<int> UnmountAsync(string driveLetter, bool ignoreOpenFiles)
    {
        return Task.Run(() => Unmount(driveLetter, ignoreOpenFiles));
    }

    public Task<bool> WipePasswordCacheAsync()
    {
        return Task.Run(WipePasswordCache);
    }

    private MountResult Mount(string volumePath, string driveLetter, byte[] passwordUtf8, int pim)
    {
        if (string.IsNullOrWhiteSpace(volumePath) || !File.Exists(volumePath))
        {
            throw new FileNotFoundException("未找到 VeraCrypt 容器文件。", volumePath);
        }

        if (passwordUtf8.Length is < 1 or > MaxPasswordBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(passwordUtf8), $"VeraCrypt 密码必须为 1-{MaxPasswordBytes} 个 UTF-8 字节。");
        }

        if (pim is < 0 or > MaxPimValue)
        {
            throw new ArgumentOutOfRangeException(nameof(pim), $"PIM 必须位于 0-{MaxPimValue}。 ");
        }

        var normalized = NormalizeDriveLetter(driveLetter);
        var driveRoot = $"{normalized}:\\";
        if (Directory.Exists(driveRoot))
        {
            throw new InvalidOperationException($"盘符 {normalized}: 已被占用。");
        }

        using var driver = OpenDriver();
        var version = GetDriverVersion(driver);
        if (version != ExpectedDriverVersion)
        {
            throw new InvalidOperationException($"VeraCrypt 驱动版本 0x{version:X4} 与 VaultLatch 支持的 0x{ExpectedDriverVersion:X4} 不一致。");
        }

        ValidateMountStructLayout();

        var request = CreateMountRequest(volumePath, normalized, passwordUtf8, pim, exclusiveAccess: true);
        var result = PerformMount(driver, request);
        if (!result.IoctlSucceeded && result.Win32Error == ErrorSharingViolation)
        {
            _logger.Info("VeraCrypt 容器无法独占打开，改用共享访问重试挂载。");
            request = CreateMountRequest(volumePath, normalized, passwordUtf8, pim, exclusiveAccess: false);
            result = PerformMount(driver, request);
        }

        _logger.Info(
            $"VeraCrypt 驱动挂载 {normalized}: 完成；ioctl={result.IoctlSucceeded}；returnCode={result.ReturnCode}；win32={result.Win32Error}；readOnlyAccessDenied={result.MountedReadOnlyAfterAccessDenied}；readOnlyWriteProtected={result.MountedReadOnlyAfterDeviceWriteProtected}；dirty={result.FilesystemDirty}。");
        return result;
    }

    private MountResult PerformMount(SafeFileHandle driver, MountStruct request)
    {
        var size = Marshal.SizeOf<MountStruct>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(request, buffer, fDeleteOld: false);
            if (!DeviceIoControl(driver, IoctlMountVolume, buffer, size, buffer, size, out var returned, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                return new MountResult(false, -1, error, false, false, false, false);
            }

            if (returned != size)
            {
                _logger.Error($"VeraCrypt 驱动挂载返回长度异常：{returned}，期望 {size}。");
            }

            var response = Marshal.PtrToStructure<MountStruct>(buffer);
            return new MountResult(
                true,
                response.ReturnCode,
                0,
                response.FilesystemDirty != 0,
                response.VolumeMountedReadOnlyAfterAccessDenied != 0,
                response.VolumeMountedReadOnlyAfterDeviceWriteProtected != 0,
                response.VolumeMasterKeyVulnerable != 0);
        }
        finally
        {
            ZeroUnmanagedBuffer(buffer, size);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static MountStruct CreateMountRequest(
        string volumePath,
        char driveLetter,
        byte[] passwordUtf8,
        int pim,
        bool exclusiveAccess)
    {
        var bytesPerSector = GetHostBytesPerSector(volumePath);
        return new MountStruct
        {
            VolumePath = volumePath,
            VolumePassword = PasswordStruct.FromBytes(passwordUtf8),
            Cache = 0,
            DosDriveNumber = driveLetter - 'A',
            BytesPerSector = bytesPerSector,
            MountReadOnly = 0,
            MountRemovable = 0,
            ExclusiveAccess = exclusiveAccess ? 1 : 0,
            MountManager = 1,
            PreserveTimestamp = 1,
            PartitionInInactiveSysEncScope = 0,
            PartitionInInactiveSysEncScopeDriveNo = 0,
            SystemFavorite = 0,
            ProtectHiddenVolume = 0,
            ProtectedHidVolPassword = PasswordStruct.Empty(),
            UseBackupHeader = 0,
            RecoveryMode = 0,
            Pkcs5Prf = 0, // Auto-detect, same as VeraCrypt default.
            ProtectedHidVolPkcs5Prf = 0,
            VolumeMountedReadOnlyAfterPartialSysEnc = 0,
            BytesPerPhysicalSector = bytesPerSector,
            VolumePim = pim,
            ProtectedHidVolPim = 0,
            Label = string.Empty,
            IsNtfs = 0,
            DriverSetLabel = 0,
            CachePim = 0,
            MaximumTransferLength = 65_536,
            MaximumPhysicalPages = 17,
            AlignmentMask = 0,
            VolumeMasterKeyVulnerable = 0,
        };
    }

    private int Unmount(string driveLetter, bool ignoreOpenFiles)
    {
        var normalized = NormalizeDriveLetter(driveLetter);
        var request = new UnmountStruct
        {
            DosDriveNumber = normalized - 'A',
            IgnoreOpenFiles = ignoreOpenFiles ? 1 : 0,
        };

        using var driver = OpenDriver();
        var size = Marshal.SizeOf<UnmountStruct>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(request, buffer, fDeleteOld: false);
            if (!DeviceIoControl(driver, IoctlUnmountVolume, buffer, size, buffer, size, out var returned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VeraCrypt driver unmount IOCTL failed.");
            }

            if (returned != size)
            {
                _logger.Error($"VeraCrypt 驱动卸载返回长度异常：{returned}，期望 {size}。");
            }

            var response = Marshal.PtrToStructure<UnmountStruct>(buffer);
            _logger.Info($"VeraCrypt 驱动{(ignoreOpenFiles ? "强制" : "正常")}卸载 {normalized}: 返回码：{response.ReturnCode}；hiddenProtection={response.HiddenVolumeProtectionTriggered}。");
            return response.ReturnCode;
        }
        finally
        {
            ZeroUnmanagedBuffer(buffer, size);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool WipePasswordCache()
    {
        using var driver = OpenDriver();
        if (!DeviceIoControl(driver, IoctlWipePasswordCache, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VeraCrypt driver cache-wipe IOCTL failed.");
        }

        _logger.Info("VeraCrypt 驱动密码缓存已擦除。");
        return true;
    }

    private static int GetDriverVersion(SafeFileHandle driver)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, 0);
            if (!DeviceIoControl(driver, IoctlGetDriverVersion, IntPtr.Zero, 0, buffer, sizeof(int), out var returned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 VeraCrypt 驱动版本失败。");
            }

            if (returned != sizeof(int))
            {
                throw new InvalidOperationException($"VeraCrypt 驱动版本返回长度异常：{returned}。");
            }

            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateMountStructLayout()
    {
        var size = Marshal.SizeOf<MountStruct>();
        if (size != ExpectedMountStructSize)
        {
            throw new InvalidOperationException($"VaultLatch MOUNT_STRUCT 布局错误：{size} bytes，期望 {ExpectedMountStructSize} bytes。");
        }

        if (Marshal.SizeOf<PasswordStruct>() != 136)
        {
            throw new InvalidOperationException("VaultLatch Password 结构布局错误。");
        }
    }

    private static uint GetHostBytesPerSector(string volumePath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(volumePath));
            if (!string.IsNullOrWhiteSpace(root)
                && GetDiskFreeSpace(root, out _, out var bytesPerSector, out _, out _)
                && bytesPerSector > 0)
            {
                return bytesPerSector;
            }
        }
        catch
        {
        }

        return 512;
    }

    private static SafeFileHandle OpenDriver()
    {
        var handle = CreateFile(
            DriverPath,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "无法打开 VeraCrypt 驱动设备。");
        }

        return handle;
    }

    private static char NormalizeDriveLetter(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
        {
            throw new ArgumentException("挂载盘符为空。", nameof(driveLetter));
        }

        var value = char.ToUpperInvariant(driveLetter.Trim().TrimEnd(':')[0]);
        if (value is < 'A' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(driveLetter), "挂载盘符无效。");
        }

        return value;
    }

    private static void ZeroUnmanagedBuffer(IntPtr buffer, int size)
    {
        if (buffer == IntPtr.Zero || size <= 0)
        {
            return;
        }

        var zeros = new byte[size];
        Marshal.Copy(zeros, 0, buffer, size);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PasswordStruct
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxPasswordBytes + 1)]
        public byte[] Text;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] Pad;

        public static PasswordStruct Empty() => new()
        {
            Length = 0,
            Text = new byte[MaxPasswordBytes + 1],
            Pad = new byte[3],
        };

        public static PasswordStruct FromBytes(ReadOnlySpan<byte> password)
        {
            var result = Empty();
            result.Length = (uint)password.Length;
            password.CopyTo(result.Text);
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct MountStruct
    {
        public int ReturnCode;
        public int FilesystemDirty;
        public int VolumeMountedReadOnlyAfterAccessDenied;
        public int VolumeMountedReadOnlyAfterDeviceWriteProtected;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string VolumePath;

        public PasswordStruct VolumePassword;
        public int Cache;
        public int DosDriveNumber;
        public uint BytesPerSector;
        public int MountReadOnly;
        public int MountRemovable;
        public int ExclusiveAccess;
        public int MountManager;
        public int PreserveTimestamp;
        public int PartitionInInactiveSysEncScope;
        public int PartitionInInactiveSysEncScopeDriveNo;
        public int SystemFavorite;
        public int ProtectHiddenVolume;
        public PasswordStruct ProtectedHidVolPassword;
        public int UseBackupHeader;
        public int RecoveryMode;
        public int Pkcs5Prf;
        public int ProtectedHidVolPkcs5Prf;
        public int VolumeMountedReadOnlyAfterPartialSysEnc;
        public uint BytesPerPhysicalSector;
        public int VolumePim;
        public int ProtectedHidVolPim;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
        public string Label;

        public int IsNtfs;
        public int DriverSetLabel;
        public int CachePim;
        public uint MaximumTransferLength;
        public uint MaximumPhysicalPages;
        public uint AlignmentMask;
        public int VolumeMasterKeyVulnerable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnmountStruct
    {
        public int DosDriveNumber;
        public int IgnoreOpenFiles;
        public int HiddenVolumeProtectionTriggered;
        public int ReturnCode;
    }

    internal readonly record struct MountResult(
        bool IoctlSucceeded,
        int ReturnCode,
        int Win32Error,
        bool FilesystemDirty,
        bool MountedReadOnlyAfterAccessDenied,
        bool MountedReadOnlyAfterDeviceWriteProtected,
        bool VolumeMasterKeyVulnerable)
    {
        public bool Success => IoctlSucceeded && ReturnCode == ErrSuccess;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
