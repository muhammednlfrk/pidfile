# PidFile

This library is helping you create, manage and use the process id files
automatically. It's easy to set-up. You can use it on your desktop, command
line and ASP.NET applications.

So what is a process id file? And can't I just white the process id on a file
howerver I want?

A process id file is a file that contains only your process id. It's there to
make sure only one instance of your program is running. Maybe your applicaton
runs on a container of it's own or it's a deamon program etc. Whatever is the
reason that you want only one instance of your program to run, PID files are
the solution.

You can write a PID into a file however you want but sometimes it's important to
make sure another instance of the program or result of a power outage etc.
doesn't courrupt the PID file. The solution is in this library.

[![CI](https://github.com/muhammednlfrk/pidfile/actions/workflows/ci.yml/badge.svg)](https://github.com/<USER>/pidfile/actions/workflows/ci.yml)

## Installation

```bash
dotnet add package PidFile
```

Targets `netstandard2.1` (.NET Core 3.0+, .NET 5+, Mono, Unity 2021.2+). .NET
Framework is not supported.

## Usage

```csharp
// TODO: minimal working example
```

## Building from source

```bash
git clone https://github.com/muhammednlfrk/pidfile.git
cd pidfile
dotnet build
dotnet test
```

The SDK version is pinned in `global.json`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: see
[SECURITY.md](SECURITY.md).

## License

See [LICENSE](LICENSE).
