# PidFile.NET

PidFile.NET helps you create and manage process ID (PID) files. It is
designed for desktop, command-line, worker, and ASP.NET applications that
should allow only one running instance.

A PID file contains the ID of the process that owns it. A PID file alone does
not prevent another process from starting, and it can become stale after a
crash or power loss. PidFile.NET combines the record with an operating-system
file lock and records the process start time, so stale records can be
distinguished from a live owner and safely replaced.

The lock is held on the same file as the record, while the record remains
readable by other processes. If the owner process exits unexpectedly, the
operating system releases the lock and the next process can acquire it.

[![CI](https://github.com/muhammednlfrk/pidfile-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/muhammednlfrk/pidfile-dotnet/actions/workflows/ci.yml)

## Installation

```bash
dotnet add package PidFile.NET
```

Targets `netstandard2.1` (.NET Core 3.0+, .NET 5+, Mono, and Unity 2021.2+).
.NET Framework is not supported.

## Usage

```csharp
using System;
using System.IO;
using PidFile.NET;

string path = Path.Combine(Path.GetTempPath(), "my-app.pid");

using (PidFile pidFile = new PidFile(path)) {
    if (!pidFile.TryAcquire()) {
        Console.Error.WriteLine("Another instance is already running.");
        return;
    }

    Console.WriteLine("This process owns the PID file.");
    // Run the application while the PidFile instance remains alive.
}
```

`TryAcquire` does not wait: it returns `false` when another process, or
another `PidFile` instance in the same process, already owns the path. The
constructor requires an absolute path; `Path.Combine` with
`Path.GetTempPath()` produces one.

Use `Inspect` when you need to determine the state of a record without
acquiring ownership:

```csharp
PidFileInspection inspection;
using (PidFile pidFile = new PidFile(path)) {
    inspection = pidFile.Inspect();
}

switch (inspection.Status) {
    case PidFileStatus.NoRecord:
        // The file is missing or empty.
        break;
    case PidFileStatus.OwnerAlive:
        // The recorded process ID and start time match a running process.
        break;
    case PidFileStatus.OwnerGone:
        // The process has exited, or the PID was reused.
        break;
    case PidFileStatus.Malformed:
        // The file does not contain a valid PidFile.NET record.
        break;
    case PidFileStatus.Unverifiable:
        // The process exists, but its details could not be checked.
        break;
}
```

`Inspect` does not require ownership. Its `Record` property is populated when
the record can be parsed, including for `OwnerGone` and `Unverifiable`
results. Dispose the `PidFile` (or call `TryRelease`) to clear the record and
release ownership. If the process crashes, the operating system releases the
lock, but the stale record remains until a later owner overwrites it.

## Building from source

```bash
git clone https://github.com/muhammednlfrk/pidfile-dotnet.git
cd pidfile-dotnet
dotnet build
dotnet test
```

The SDK version is pinned in `global.json`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: see
[SECURITY.md](SECURITY.md).

## License

See [LICENSE](LICENSE).
