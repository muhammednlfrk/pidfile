# Changelog

All notable changes to PidFile.NET are documented here.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and [Semantic Versioning](https://semver.org/). No release has been published
yet, so all current work is recorded under `Unreleased`.

## [Unreleased]

### Added

- `PidFile` and `IPidFile` APIs for single-instance ownership.
- `PidRecord`, `PidFileInspection`, and `PidFileStatus` types.
- PID and UTC process-start-time records for detecting PID reuse.
- Non-blocking acquisition and explicit release operations.
- Inspection statuses for missing, malformed, live, stale, and unverifiable
  records.
- Same-process and cross-process ownership tests.
- CI validation on Linux, Windows, and macOS.

### Changed

- Ownership uses an operating-system lock while keeping the PID record
  readable by other processes.
- macOS uses an exclusive `<pid-file>.lock` sidecar file because byte-range
  locking is not supported there; other supported platforms use a byte-range
  lock on the PID file.
- Stale records can be replaced after the operating system releases a lock
  following an unexpected process exit.
- Public API changes are tracked in `PublicAPI.Shipped.txt` and
  `PublicAPI.Unshipped.txt`.

### Documentation

- Added the usage, inspection, file-format, locking, and pre-release guidance
  to the README.
- Corrected documentation, package metadata, and build instructions.

## Release history

No releases have been published yet.
