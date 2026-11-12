# PidFile.NET

[![CI](https://github.com/muhammednlfrk/pidfile-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/muhammednlfrk/pidfile-dotnet/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/muhammednlfrk/pidfile-dotnet.svg)](https://github.com/muhammednlfrk/pidfile-dotnet/blob/main/LICENSE)
[![Target framework](https://img.shields.io/badge/target-netstandard2.1-512BD4.svg)](https://dotnet.microsoft.com/platform/dotnet-standard)

> **Pre-release:** PidFile.NET has not been published to NuGet yet. The public
> API and package metadata may change before the first release.

PidFile.NET provides a cross-platform, single-instance guard backed by a PID
file. It records the owning process ID and start time, uses an operating-system
lock to prevent concurrent ownership, and detects stale records after a crash
or PID reuse.

## Features

- Non-blocking `TryAcquire` and explicit `TryRelease` operations.
- Cross-process ownership protection.
- Same-process protection for multiple `PidFile` instances using the same path.
- PID reuse detection using both process ID and process start time.
- Inspection without acquiring ownership.
- Readable records while another process owns the file.
- `netstandard2.1` target for .NET Core 3.0+, .NET 5+, Mono, and Unity
  2021.2+.

## Installation

The first NuGet release is not available yet. To use the library from source,
reference the project from your solution or build the repository locally.

The package targets `netstandard2.1`; .NET Framework is not supported.

## Usage

Pass an absolute path to the constructor. `TryAcquire` returns immediately:
`true` means this instance owns the file, and `false` means another owner
already holds it.

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
    // Keep pidFile alive while the application is running.
}
```

Disposing the instance releases ownership and clears the record. A process
that exits unexpectedly leaves its record behind, but the operating system
releases its lock. A later process can inspect the stale record and acquire
the file.

## Inspecting a PID file

`Inspect` does not require ownership. It returns a `PidFileInspection` with a
`Status` and, when the record can be parsed, a `Record`.

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
        // The PID and start time match a running process.
        break;
    case PidFileStatus.OwnerGone:
        // The process exited or the PID was reused.
        break;
    case PidFileStatus.Malformed:
        // The file does not contain a valid record.
        break;
    case PidFileStatus.Unverifiable:
        // The process exists, but its details could not be checked.
        break;
}
```

| Status | Meaning |
| --- | --- |
| `NoRecord` | The file is missing or empty. |
| `Malformed` | The file cannot be parsed as a PidFile.NET record. |
| `OwnerAlive` | The recorded PID and start time match a running process. |
| `OwnerGone` | The process exited or the PID belongs to another process. |
| `Unverifiable` | The record was read, but process details could not be checked. |

The default start-time tolerance is two seconds. It can be changed through
the optional `startTimeTolerance` constructor argument.

## File format and locking

The PID file contains two ASCII lines:

1. The process ID.
2. The process start time as a UTC Windows FILETIME value.

On platforms that support byte-range locking, ownership is locked on a byte
far beyond the record so other processes can still read the record. On macOS,
ownership uses an exclusive lock file at `<pid-file>.lock`; the PID record
remains in the original file. The lock file may remain after release, but its
lock is released automatically when the owning process exits.

When byte-range locking is used, do not open and close the PID file yourself
while a `PidFile` instance owns it. On Unix-like systems, closing another
descriptor for the same file can release the owner’s lock unexpectedly. Use
`Inspect` to read a record from the owning process.

## Building from source

```bash
git clone https://github.com/muhammednlfrk/pidfile-dotnet.git
cd pidfile-dotnet
dotnet restore
dotnet build
dotnet test
```

The SDK version is pinned in `global.json`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Please report security issues
privately as described in [SECURITY.md](SECURITY.md).

## License

PidFile.NET is licensed under the [MIT License](LICENSE).
