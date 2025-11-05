using System;
using System.Text;
using Xunit;
using Shanewas.PosixIpc;

namespace PosixIpc.Tests
{
    public class PosixIpcHelperTests
    {
        private static bool IsLinux => OperatingSystem.IsLinux();

        private static int NewKey() =>
            unchecked((int)(0x5A5A0000 ^ Environment.TickCount ^ Guid.NewGuid().GetHashCode()));

        [Fact]
        public void Constructor_Throws_On_NonPositive_Size()
        {
            if (!IsLinux) return; // no-op on non-Linux

            Assert.Throws<ArgumentException>(() => new PosixIpcHelper(NewKey(), 0));
            Assert.Throws<ArgumentException>(() => new PosixIpcHelper(NewKey(), -1));
        }

        [Fact]
        public void WriteRead_String_Then_Bytes()
        {
            if (!IsLinux) return;

            int key = NewKey();
            using var shm = new PosixIpcHelper(key, 4096);

            int sem = PosixIpcHelper.CreateSemaphore(key + 1);

            // prime semaphore so Wait doesn't block
            PosixIpcHelper.SemaphoreSignal(sem);

            PosixIpcHelper.SemaphoreWait(sem);
            shm.Write("hello");
            PosixIpcHelper.SemaphoreSignal(sem);

            PosixIpcHelper.SemaphoreWait(sem);
            var s = shm.ReadString();
            PosixIpcHelper.SemaphoreSignal(sem);

            Assert.Equal("hello", s);

            // bytes path
            var bytes = Encoding.UTF8.GetBytes("bytes!");
            PosixIpcHelper.SemaphoreWait(sem);
            shm.Write(bytes);
            PosixIpcHelper.SemaphoreSignal(sem);

            PosixIpcHelper.SemaphoreWait(sem);
            var outBytes = shm.Read();
            PosixIpcHelper.SemaphoreSignal(sem);

            Assert.Equal(bytes, outBytes);

            // cleanup
            PosixIpcHelper.DestroySemaphore(sem);
        }

        [Fact]
        public void AttachExisting_Works_And_Detach_Does_Not_Remove()
        {
            if (!IsLinux) return;

            int key = NewKey();
            using var owner = new PosixIpcHelper(key, 2048);

            using (var attach = PosixIpcHelper.AttachExisting(key))
            {
                attach.Write("ok");
                var back = attach.ReadString();
                Assert.Equal("ok", back);
            }

            // owner still valid after attached instance disposed
            owner.Write("still-here");
            Assert.Equal("still-here", owner.ReadString());
        }

        [Fact]
        public void Dispose_Removes_Segment_When_Owner()
        {
            if (!IsLinux) return;

            int key = NewKey();
            var owner = new PosixIpcHelper(key, 1024);
            owner.Dispose();

            // After owner disposed, attaching should fail (segment removed)
            var ex = Assert.Throws<InvalidOperationException>(() => PosixIpcHelper.AttachExisting(key));
            Assert.Contains("does not exist", ex.Message);
        }

        [Fact]
        public void Semaphore_Create_Wait_Signal_Destroy_Is_Idempotent()
        {
            if (!IsLinux) return;

            int key = NewKey();
            int sem = PosixIpcHelper.CreateSemaphore(key);

            // signal then wait (non-blocking)
            PosixIpcHelper.SemaphoreSignal(sem);
            PosixIpcHelper.SemaphoreWait(sem);

            // destroy twice to hit tolerant error path
            PosixIpcHelper.DestroySemaphore(sem);
            PosixIpcHelper.DestroySemaphore(sem);
        }

        [Fact]
        public void Hash_Is_Deterministic_And_Empty_Is_Zero()
        {
            int h1 = PosixIpcHelper.GetDeterministicHashCode("abc");
            int h2 = PosixIpcHelper.GetDeterministicHashCode("abc");
            int hEmpty = PosixIpcHelper.GetDeterministicHashCode("");

            Assert.Equal(h1, h2);
            Assert.Equal(0, hEmpty);
        }

        [Fact]
        public void Write_Throws_When_Data_Exceeds_Allocated_Size()
        {
            if (!IsLinux) return;

            int key = NewKey();
            using var shm = new PosixIpcHelper(key, 8); // 4 bytes header + payload

            // payload larger than 4 bytes available
            Assert.Throws<InvalidOperationException>(() =>
                shm.Write(new byte[16]));
        }
    }
}
