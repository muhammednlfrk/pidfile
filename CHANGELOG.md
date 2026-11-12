# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- PID file ownership through the `PidFile` and `IPidFile` APIs.
- PID records containing the process ID and UTC process start time.
- PID file inspection with `NoRecord`, `Malformed`, `OwnerAlive`,
  `OwnerGone`, and `Unverifiable` statuses.
- Cross-process and same-process ownership tests.
- CI coverage for pushes and pull requests targeting `dev/**` branches.

### Changed

- Ownership is enforced with an operating-system file lock while keeping the
  record readable by other processes.
- Stale records are detected using both the recorded process ID and start time,
  allowing a new owner to safely replace them after a crash or PID reuse.
- Public API changes are tracked with the shipped and unshipped API files.

### Fixed

- Corrected documentation typos, package metadata, and the build instructions.
- Added usage, inspection, and ownership-lifecycle guidance to the README.
