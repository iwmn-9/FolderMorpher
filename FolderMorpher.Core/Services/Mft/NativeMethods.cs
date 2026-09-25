using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AstraSize.Services.Mft
{
    public static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

        public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
        public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [StructLayout(LayoutKind.Sequential)]
        public struct NTFS_VOLUME_DATA_BUFFER
        {
            public ulong VolumeSerialNumber;
            public ulong NumberSectors;
            public ulong TotalClusters;
            public ulong FreeClusters;
            public ulong TotalReserved;
            public uint BytesPerSector;
            public uint BytesPerCluster;
            public uint BytesPerFileRecordSegment;
            public uint ClustersPerFileRecordSegment;
            public ulong MftValidDataLength;
            public ulong MftStartLcn;
            public ulong Mft2StartLcn;
            public ulong MftZoneStart;
            public ulong MftZoneEnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTING_VCN_INPUT_BUFFER
        {
            public ulong StartingVcn;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RETRIEVAL_POINTERS_BUFFER
        {
            public uint ExtentCount;
            public ulong StartingVcn;
            // Extents follow: [Vcn, Lcn] pairs
        }
    }
}
