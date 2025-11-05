## Shanewas.PosixIpc
_A lightweight .NET library for System V shared memory and semaphores on Linux (x64, glibc)._
- Runtime: Linux only (throws on non-Linux).
- No native payloads; P/Invoke to `libc.so.6`.
- Designed for same-host, low-latency IPC between processes.

## Install
[![NuGet](https://img.shields.io/nuget/v/Shanewas.PosixIpc.svg)](https://www.nuget.org/packages/Shanewas.PosixIpc/)
````markdown
dotnet add package Shanewas.PosixIpc
````

## Quick start

```csharp
using Shanewas.PosixIpc;

const int shmKey = 0x5A5A1234;
const int semKey = 0x5A5A1235;

using var shm = new PosixIpcHelper(shmKey, 4096);
int sem = PosixIpcHelper.CreateSemaphore(semKey);

// writer
PosixIpcHelper.SemaphoreWait(sem);
shm.Write("hello");
PosixIpcHelper.SemaphoreSignal(sem);

// reader
PosixIpcHelper.SemaphoreWait(sem);
string s = shm.ReadString();
PosixIpcHelper.SemaphoreSignal(sem);

// cleanup when you own the semaphore
PosixIpcHelper.DestroySemaphore(sem);
```

## API

* `new PosixIpcHelper(int key, int size)` — create or open a shared memory segment.
* `static PosixIpcHelper AttachExisting(int key)` — attach to an existing segment.
* `void Write(byte[] data)` / `void Write(string s)` — write with a 4-byte length header.
* `byte[] Read()` / `string ReadString()` — read last written payload.
* `static int CreateSemaphore(int key)` — create/open SysV semaphore (1 slot, 0600).
* `static void SemaphoreWait(int semid)` — blocking wait with EINTR retry.
* `static void SemaphoreSignal(int semid)` — signal.
* `static void DestroySemaphore(int semid)` — IPC_RMID with tolerant errors.
* `void Dispose()` — detaches and removes SHM if owned.

## Requirements and limits

* **glibc** only (`libc.so.6`). Alpine/musl unsupported.
* Containers: cross-pod not possible; cross-container requires shared IPC namespace or `--ipc=host`.
* Kernel tunables may be required:

  * `kernel.shmmax`, `kernel.shmall` for segment/total size
  * `kernel.sem` for semaphore limits

## Notes

* Concurrency: `Write`/`Read` require external semaphore discipline.
* `AttachExisting` uses a brittle `shmctl(..., IPC_STAT, ...)` probe (x86_64).
* Timeouts: no `semtimedop` yet.

## Versioning

Semantic Versioning — breaking changes bump **major**.

## License

MIT © 2025 Shanewas
