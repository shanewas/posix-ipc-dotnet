//=============================================================================
// PosixIpcHelper
// Copyright (c) 2025 Shanewas
// License: MIT
//=============================================================================
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Shanewas.PosixIpc
{
    /// <summary>
    /// Helper for Linux/Unix inter-process communication (IPC).
    /// Provides access to System V Shared Memory and Semaphores.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public class PosixIpcHelper : IDisposable
    {
        //=============================================================================
        // P/Invoke Definitions
        //=============================================================================

        // Shared Memory
        private const int IPC_CREAT = 0x00000200;
        private const int IPC_RMID  = 0;
        private const int IPC_STAT  = 2;

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int shmget(int key, int size, int shmflg);
        [DllImport("libc.so.6", SetLastError = true)]
        private static extern IntPtr shmat(int shmid, IntPtr shmaddr, int shmflg);
        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int shmdt(IntPtr shmaddr);
        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int shmctl(int shmid, int cmd, IntPtr buf);

        // Semaphores
        private const int IPC_EXCL   = 0x00000400;
        private const int PERM_0600  = 0x180; // 0600
        private const int PERM_0666  = 0x1B6; // 0666
        private const int EEXIST     = 17;
        private const int SETVAL     = 16;
        private const int GETVAL     = 12;

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int semget(int key, int nsems, int semflg);
        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int semop(int semid, ref Sembuf sops, int nsops);
        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int semctl(int semid, int semnum, int cmd, int val);
        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int semctl(int semid, int semnum, int cmd, IntPtr buf);

        [StructLayout(LayoutKind.Sequential)]
        private struct Sembuf
        {
            public ushort sem_num;
            public short  sem_op;
            public short  sem_flg;
        }

        // Errors
        private const int EINTR = 4;   // Interrupted system call
        private const int ENOENT = 2;  // No such file or directory
        private const int EACCES = 13; // Permission denied
        private const int EINVAL = 22; // Invalid argument
        private const int EIDRM = 43;  // Identifier removed
        private const int ERANGE = 34; // Result out of range

        //=============================================================================
        // Private State
        //=============================================================================

        private int _shmid = -1;
        private IntPtr _shmaddr = IntPtr.Zero;
        private int _allocatedSize = 0;
        private bool _disposed = false;
        private bool _ownsShm;

        //=============================================================================
        // Constructors
        //=============================================================================

        /// <summary>Creates or gets a shared memory segment.</summary>
        public PosixIpcHelper(int key, int size)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Linux only.");
            if (size <= 0) throw new ArgumentException("Size must be positive", nameof(size));

            _ownsShm = true;
            _allocatedSize = size;

            _shmid = shmget(key, size, IPC_CREAT | PERM_0600);
            if (_shmid == -1) HandleError($"shmget failed for key {key}");

            _shmaddr = shmat(_shmid, IntPtr.Zero, 0);
            if (_shmaddr == (IntPtr)(-1))
            {
                shmctl(_shmid, IPC_RMID, IntPtr.Zero);
                HandleError($"shmat failed for shmid {_shmid}");
            }
        }

        /// <summary>Private constructor for AttachExisting.</summary>
        private PosixIpcHelper()
        {
            _ownsShm = false;
            _disposed = false;
        }

        //=============================================================================
        // Shared Memory Operations
        //=============================================================================

        public void Write(string data) => Write(Encoding.UTF8.GetBytes(data));

        public void Write(byte[] data)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PosixIpcHelper));
            if (data.Length + sizeof(int) > _allocatedSize)
                throw new InvalidOperationException($"Data size {data.Length + sizeof(int)} exceeds allocated size {_allocatedSize}");

            Marshal.WriteInt32(_shmaddr, data.Length);
            Marshal.Copy(data, 0, IntPtr.Add(_shmaddr, sizeof(int)), data.Length);
        }

        public string ReadString() => Encoding.UTF8.GetString(Read());

        public byte[] Read()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PosixIpcHelper));
            int size = Marshal.ReadInt32(_shmaddr);
            if (size < 0) throw new InvalidOperationException("Invalid data size in shared memory.");
            var dataBuffer = new byte[size];
            Marshal.Copy(IntPtr.Add(_shmaddr, sizeof(int)), dataBuffer, 0, size);
            return dataBuffer;
        }

        /// <summary>Attach to an existing shared memory segment.</summary>
        public static PosixIpcHelper AttachExisting(int key)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Linux only.");

            var helper = new PosixIpcHelper
            {
                _shmid = shmget(key, 0, 0)
            };
            if (helper._shmid == -1)
                HandleError($"Shared memory {key} does not exist");

            helper._shmaddr = shmat(helper._shmid, IntPtr.Zero, 0);
            if (helper._shmaddr == (IntPtr)(-1))
                HandleError($"Failed to attach to shared memory {key}");

            helper._ownsShm = false;

            int detected = 0;
            if (IntPtr.Size == 8)
            {
                IntPtr buf = IntPtr.Zero;
                try
                {
                    buf = Marshal.AllocHGlobal(256);
                    if (shmctl(helper._shmid, IPC_STAT, buf) != -1)
                    {
                        // NOTE: brittle offset for x86_64 glibc; replace with a proper shmid_ds struct later.
                        long segsz = Marshal.ReadInt64(buf, 48);
                        if (segsz >= 4096 && segsz <= int.MaxValue) detected = (int)segsz;
                    }
                }
                finally
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                }
            }

            helper._allocatedSize = detected > 0 ? detected : 1024 * 1024 * 1024;
            return helper;
        }

        //=============================================================================
        // Semaphores
        //=============================================================================

        public static int CreateSemaphore(int key)
        {
            int semid = semget(key, 1, IPC_CREAT | IPC_EXCL | PERM_0600);
            if (semid == -1)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == EEXIST)
                {
                    semid = semget(key, 1, PERM_0600);
                    if (semid == -1)
                    {
                        if (Marshal.GetLastWin32Error() == ENOENT)
                        {
                            semid = semget(key, 1, IPC_CREAT | IPC_EXCL | PERM_0600);
                            if (semid == -1) HandleError($"semget(recreate) failed for key {key}");
                            if (semctl(semid, 0, SETVAL, 0) == -1) HandleError("semctl SETVAL failed");
                        }
                        else HandleError($"semget(open) failed for key {key}");
                    }
                }
                else HandleError($"semget(create) failed for key {key}");
            }
            else
            {
                if (semctl(semid, 0, SETVAL, 0) == -1) HandleError("semctl SETVAL failed");
            }

            try
            {
                if (semctl(semid, 0, GETVAL, IntPtr.Zero) < 0)
                    if (semctl(semid, 0, SETVAL, 0) == -1) HandleError("semctl SETVAL(repair) failed");
            }
            catch { /* ignore */ }

            return semid;
        }

        /// <summary>Wait for semaphore (blocking, retried on EINTR).</summary>
        public static void SemaphoreWait(int semid)
        {
            var buf = new Sembuf { sem_num = 0, sem_op = -1, sem_flg = 0 };
            int result;
            do
            {
                result = semop(semid, ref buf, 1);
                if (result == -1)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == EINTR) continue;
                    if (error == EIDRM) throw new OperationCanceledException($"Semaphore {semid} removed (EIDRM). Recreate and retry.");
                    HandleError($"semop WAIT failed for semid {semid}");
                }
            } while (result == -1);
        }

        /// <summary>Signal semaphore.</summary>
        public static void SemaphoreSignal(int semid)
        {
            var buf = new Sembuf { sem_num = 0, sem_op = 1, sem_flg = 0 };
            int result;
            do
            {
                result = semop(semid, ref buf, 1);
                if (result == -1)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == EINTR) continue;
                    if (error == EIDRM) throw new OperationCanceledException($"Semaphore {semid} removed (EIDRM). Recreate and retry.");
                    HandleError($"semop SIGNAL failed for semid {semid}");
                }
            } while (result == -1);
        }

        public static void DestroySemaphore(int semid)
        {
            if (semctl(semid, 0, IPC_RMID, IntPtr.Zero) == -1)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != EINVAL && error != ENOENT)
                    Console.Error.WriteLine($"Warning: semctl IPC_RMID failed for semid {semid}, error: {error}");
            }
        }

        //=============================================================================
        // Utility
        //=============================================================================

        /// <summary>FNV-1a hash, deterministic, non-crypto.</summary>
        public static int GetDeterministicHashCode(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;

            unchecked
            {
                const uint FnvPrime = 16777619;
                const uint FnvOffsetBasis = 2166136261;
                uint hash = FnvOffsetBasis;
                foreach (char c in str)
                {
                    hash ^= c;
                    hash *= FnvPrime;
                }
                return Math.Abs((int)hash);
            }
        }

        //=============================================================================
        // Resource Management
        //=============================================================================

        public void Dispose()
        {
            if (_disposed) return;

            if (_shmaddr != IntPtr.Zero)
            {
                shmdt(_shmaddr);
                _shmaddr = IntPtr.Zero;
            }
            if (_shmid != -1 && _ownsShm)
            {
                shmctl(_shmid, IPC_RMID, IntPtr.Zero);
                _shmid = -1;
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~PosixIpcHelper()
        {
            try { Dispose(); } catch { }
        }

        private static void HandleError(string message)
        {
            int error = Marshal.GetLastWin32Error();
            string errorMessage = error switch
            {
                EINTR  => "The operation was interrupted by a system signal.",
                ENOENT => "No such segment/semaphore exists.",
                EACCES => "Permission denied.",
                EINVAL => "Invalid parameter.",
                EIDRM  => "Semaphore/shared memory was destroyed while in use.",
                ERANGE => "Result out of range.",
                _      => $"System error code: {error}"
            };
            throw new InvalidOperationException($"{message}. Details: {errorMessage}");
        }
    }
}
