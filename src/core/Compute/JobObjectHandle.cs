using System;
using System.Runtime.InteropServices;

namespace backs2.Core.Compute;

/// <summary>
/// Wraps a Windows Job Object configured with JOB_OBJECT_LIMIT_PROCESS_MEMORY.
/// Assigning a child process to the job imposes the memory cap; closing the
/// handle terminates any straggler processes. No-op on non-Windows — the
/// caller degrades gracefully when TryAttach returns false.
/// </summary>
public sealed class JobObjectHandle : IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitProcessMemory = 0x0100;

    private IntPtr _handle;

    public bool TryAttach(IntPtr processHandle, int memoryLimitMb, out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "Job Objects are Windows-only";
            return false;
        }

        if (memoryLimitMb <= 0)
        {
            error = "memoryLimitMb must be positive";
            return false;
        }

        if (_handle == IntPtr.Zero)
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                error = $"CreateJobObject failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitProcessMemory
                },
                ProcessMemoryLimit = (UIntPtr)((ulong)memoryLimitMb * 1024UL * 1024UL)
            };

            int infoSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, infoPtr, (uint)infoSize))
                {
                    error = $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}";
                    CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }

        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            error = $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // ---- Win32 ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, uint infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}