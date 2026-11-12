using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PidFile.NET {
    /// <summary>
    /// Defines the interface for a PID file, which includes methods for
    /// acquiring and releasing ownership, as well as inspecting the PID
    /// file's status.
    /// </summary>
    public interface IPidFile : IDisposable {
        /// <summary>
        /// Gets the fully qualified path of the PID file.
        /// </summary>
        string FilePath { get; }

        /// <summary>
        /// Gets the record this instance writes when it owns the PID file.
        /// </summary>
        PidRecord Record { get; }

        /// <summary>
        /// Attempts to become the owner of the PID file without blocking.
        /// </summary>
        /// <returns>
        /// <c>true</c> if this instance now owns the PID file; <c>false</c> if
        /// another live process (or another <see cref="IPidFile"/> in this
        /// process) already owns it.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when this instance already owns the PID file.
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when this instance has been disposed.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown on I/O failures that are not lock conflicts, such as a
        /// missing directory.
        /// </exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the file cannot be opened due to missing permissions.
        /// </exception>
        bool TryAcquire();

        /// <summary>
        /// Releases ownership and clears the record. The file itself is kept.
        /// </summary>
        /// <returns>
        /// <c>true</c> if ownership was released; <c>false</c> if this
        /// instance did not own the PID file.
        /// </returns>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when this instance has been disposed.
        /// </exception>
        bool TryRelease();

        /// <summary>
        /// Reads the record from the PID file and verifies it against the
        /// running processes (PID and start time). Does not require ownership.
        /// </summary>
        /// <remarks>
        /// A <see cref="PidFileStatus.Malformed"/> result can be transient: a
        /// reader may observe a record while its owner is still writing it.
        /// </remarks>
        /// <returns>
        /// A <see cref="PidFileInspection"/> with the status and, when the
        /// record could be parsed, the record itself.
        /// </returns>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when this instance has been disposed.
        /// </exception>
        PidFileInspection Inspect();
    }

    /// <summary>
    /// Single-instance guard backed by one text file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// File layout (ASCII, two lines): the process ID on the first line and
    /// the process start time (Windows FILETIME, UTC) on the second line.
    /// Tools that read only the first line (systemd's PIDFile=,
    /// <c>read pid &lt; file</c>) work as usual.
    /// </para>
    /// <para>
    /// Ownership is a byte-range lock on a single byte far beyond the record
    /// where supported. macOS uses an advisory whole-file lock because
    /// FileStream byte-range locks are not supported there. The record stays
    /// readable by every process, and the kernel releases the lock when the
    /// owner dies.
    /// </para>
    /// <para>
    /// Do not open and close the PID file with your own code inside the
    /// owning process: on Unix, closing any descriptor of a file drops the
    /// process's byte-range locks on it.
    /// </para>
    /// </remarks>
    public sealed class PidFile : IPidFile {
        // The locked byte lives far beyond the record. Windows byte-range
        // locks are mandatory, so locking the record region itself would make
        // it unreadable for other processes.
        private const long LOCK_POSITION = 1L << 40;
        private const long LOCK_LENGTH = 1L;

        // Buffering disabled: every write goes straight to the file.
        private const int BUFFER_SIZE = 1;

        // Two lines: up to 10 digits + up to 19 digits + line endings.
        private const int MAX_RECORD_BYTES = 64;
        private const int RECORD_LINES = 2;

        private static readonly TimeSpan DEFAULT_TOLERANCE = TimeSpan.FromSeconds(2);

        // Unix fcntl locks are per-process: two instances in the same process
        // would not exclude each other. This registry covers that case.
        // Lock order: _lock -> REGISTRY_GATE. Never the reverse.
        private static readonly object REGISTRY_GATE = new object();
        private static readonly Dictionary<string, PidFile> OWNERS =
            new Dictionary<string, PidFile>(pathComparer());

        private readonly object _lock = new object();
        private readonly string _fPath;
        private readonly PidRecord _rec;
        private readonly long _stt; // Start time tolerance in ticks (100 ns)

        private bool _disposed;
        private FileStream? _fs;
        private bool _usesNativeLock;

        /// <summary>
        /// Initializes a new instance for the current process.
        /// </summary>
        /// <param name="filePath">
        /// The fully qualified path of the PID file.
        /// </param>
        /// <param name="startTimeTolerance">
        /// The tolerance used when comparing start times. Defaults to 2
        /// seconds.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the path is null, empty, whitespace, or not fully
        /// qualified.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the tolerance is negative.
        /// </exception>
        public PidFile(
            string filePath,
            TimeSpan? startTimeTolerance = null)
            : this(filePath, readSelf(), startTimeTolerance) {
        }

        // Test seam: lets tests simulate PID reuse deterministically. Expose
        // it with <InternalsVisibleTo Include="PidFile.NET.Tests" />.
        internal PidFile(
            string filePath,
            int processId,
            DateTime startTimeUtc,
            TimeSpan? startTimeTolerance = null)
            : this(filePath, (processId, startTimeUtc), startTimeTolerance) {
        }

        private PidFile(
            string filePath,
            (int Pid, DateTime Start) self,
            TimeSpan? startTimeTolerance) {
            if (string.IsNullOrWhiteSpace(filePath)) {
                throw new ArgumentException(
                    "File path cannot be null or whitespace.",
                    nameof(filePath));
            }
            if (!Path.IsPathFullyQualified(filePath)) {
                throw new ArgumentException(
                    "File path must be an absolute path.",
                    nameof(filePath));
            }
            if (self.Pid <= 0) {
                throw new ArgumentOutOfRangeException(
                    nameof(self),
                    "Process ID must be a positive integer.");
            }
            if (self.Start.Kind != DateTimeKind.Utc) {
                throw new ArgumentException(
                    "Start time must be in UTC.",
                    nameof(self));
            }

            TimeSpan tolerance = startTimeTolerance ?? DEFAULT_TOLERANCE;
            if (tolerance < TimeSpan.Zero) {
                throw new ArgumentOutOfRangeException(
                    nameof(startTimeTolerance),
                    "Start time tolerance must be non-negative.");
            }

            _fPath = Path.GetFullPath(filePath);
            _rec = new PidRecord(self.Pid, self.Start);
            _stt = tolerance.Ticks;
        }

        #region Properties

        /// <inheritdoc/>
        public string FilePath { get => _fPath; }

        /// <inheritdoc/>
        public PidRecord Record { get => _rec; }

        #endregion

        #region Public Methods

        /// <inheritdoc/>
        public bool TryAcquire() {
            lock (_lock) {
                throwIfDisposed();
                if (_fs != null) {
                    throw new InvalidOperationException(
                        "The PID file is already acquired.");
                }

                // In-process exclusion first. No file is opened (and later
                // closed) when this process already owns the path, so we can
                // never drop the owner's fcntl lock by accident.
                lock (REGISTRY_GATE) {
                    if (OWNERS.ContainsKey(_fPath)) {
                        return false;
                    }
                    OWNERS.Add(_fPath, this);
                }

                FileStream? fs = null;
                bool acquired = false;
                try {
                    // Errors here (missing directory, permissions, disk) are
                    // real errors and propagate to the caller.
                    fs = new FileStream(
                        path: _fPath,
                        mode: FileMode.OpenOrCreate,
                        access: FileAccess.ReadWrite,
                        share: FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: BUFFER_SIZE);

                    if (!tryLock(fs)) {
                        return false;
                    }
                    _usesNativeLock = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

                    // We hold the lock: whatever the file contained is stale.
                    writeRecord(fs);
                    _fs = fs;
                    acquired = true;
                    return true;
                } finally {
                    if (!acquired) {
                        fs?.Dispose();
                        unregister();
                    }
                }
            }
        }

        /// <inheritdoc/>
        public PidFileInspection Inspect() {
            throwIfDisposed();

            byte[]? data;
            try {
                data = readRecordBytes();
            } catch (FileNotFoundException) {
                return new PidFileInspection(PidFileStatus.NoRecord, null);
            } catch (IOException ex) when (!isPathProblem(ex)) {
                return new PidFileInspection(PidFileStatus.Unverifiable, null);
            } catch (UnauthorizedAccessException) {
                return new PidFileInspection(PidFileStatus.Unverifiable, null);
            }

            if (data.Length == 0) {
                return new PidFileInspection(PidFileStatus.NoRecord, null);
            }

            PidRecord? record = parseRecord(data);
            if (record == null) {
                return new PidFileInspection(PidFileStatus.Malformed, null);
            }

            return verify(record.Value);
        }

        /// <inheritdoc/>
        public bool TryRelease() {
            lock (_lock) {
                throwIfDisposed();
                return releaseCore();
            }
        }

        /// <inheritdoc/>
        public void Dispose() {
            lock (_lock) {
                if (_disposed) {
                    return;
                }
                _disposed = true;

                try {
                    releaseCore();
                } catch (IOException) {
                    // Dispose must not throw.
                }
            }
        }

        #endregion

        #region Private Methods

        private bool releaseCore() {
            FileStream? fs = _fs;
            if (fs == null) {
                return false;
            }

            try {
                try {
                    // Clear the record BEFORE unlocking: after the unlock a
                    // new owner may write its own record, which we must not
                    // touch.
                    fs.SetLength(0);
                    fs.Flush(flushToDisk: true);
                } catch (IOException) {
                    // Best effort: a stale record is overwritten by the next
                    // owner and fails verification anyway.
                }

                unlock(fs);
            } finally {
                _fs = null;
                _usesNativeLock = false;
                fs.Dispose();

                // Unregister only after the descriptor is closed. Otherwise
                // another instance could lock the file first and our close
                // would drop ITS lock (POSIX semantics).
                unregister();
            }
            return true;
        }

        private void writeRecord(FileStream fs) {
            string text = _rec.ProcessId.ToString("D", CultureInfo.InvariantCulture)
                + "\n"
                + _rec.StartTimeUtc.ToFileTimeUtc().ToString("D", CultureInfo.InvariantCulture)
                + "\n";
            byte[] bytes = Encoding.ASCII.GetBytes(text);

            // Write first, then trim: never a long empty window for readers.
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write(bytes, 0, bytes.Length);
            fs.SetLength(bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        private static bool tryLock(FileStream fs) {
            try {
                fs.Lock(LOCK_POSITION, LOCK_LENGTH);
                return true;
            } catch (IOException) {
                // The file opened fine, so a failure of Lock itself means the
                // byte is locked by another process.
                return false;
            } catch (UnauthorizedAccessException) {
                // Some platforms report a lock conflict this way.
                return false;
            } catch (PlatformNotSupportedException) when (
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                return tryNativeLock(fs);
            }
        }

        private static bool tryNativeLock(FileStream fs) {
            int result = flock(
                fs.SafeFileHandle.DangerousGetHandle(),
                LOCK_EX | LOCK_NB);
            if (result == 0) {
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            if (error == EWOULDBLOCK) {
                return false;
            }

            throw new IOException(
                "The PID file could not be locked.",
                new Win32Exception(error));
        }

        private void unlock(FileStream fs) {
            if (_usesNativeLock) {
                if (flock(
                    fs.SafeFileHandle.DangerousGetHandle(),
                    LOCK_UN) != 0) {
                    // Closing the stream releases the lock anyway.
                }
                return;
            }

            try {
                fs.Unlock(LOCK_POSITION, LOCK_LENGTH);
            } catch (IOException) {
                // Closing the stream releases the lock anyway.
            }
        }

        private byte[] readRecordBytes() {
            // If a PidFile in THIS process owns the path, read through its
            // stream. Opening and closing another descriptor here would
            // silently drop that owner's fcntl lock on Unix.
            PidFile? owner;
            lock (REGISTRY_GATE) {
                OWNERS.TryGetValue(_fPath, out owner);
            }
            if (owner != null) {
                byte[]? own = owner.readOwnBytes();
                if (own != null) {
                    return own;
                }
                // Owner released in the meantime: fall through to the file.
            }

            using (FileStream fs = new FileStream(
                path: _fPath,
                mode: FileMode.Open,
                access: FileAccess.Read,
                share: FileShare.ReadWrite | FileShare.Delete,
                bufferSize: BUFFER_SIZE)) {
                return readAll(fs);
            }
        }

        private byte[]? readOwnBytes() {
            lock (_lock) {
                if (_fs == null) {
                    return null;
                }
                return readAll(_fs);
            }
        }

        private static byte[] readAll(FileStream fs) {
            // Read one byte more than the maximum so oversized files are
            // detected without reading them completely.
            byte[] buffer = new byte[MAX_RECORD_BYTES + 1];
            int total = 0;

            fs.Seek(0, SeekOrigin.Begin);
            while (total < buffer.Length) {
                int read = fs.Read(buffer, total, buffer.Length - total);
                if (read == 0) {
                    break;
                }
                total += read;
            }

            byte[] result = new byte[total];
            Buffer.BlockCopy(buffer, 0, result, 0, total);
            return result;
        }

        private static PidRecord? parseRecord(byte[] data) {
            if (data.Length > MAX_RECORD_BYTES) {
                return null;
            }

            // "<pid>\n<filetime>\n" splits into three parts, the last one empty.
            string[] lines = Encoding.ASCII.GetString(data).Split('\n');
            int count = lines.Length;
            if (count == RECORD_LINES + 1 && lines[RECORD_LINES].Length == 0) {
                count = RECORD_LINES;
            }
            if (count != RECORD_LINES) {
                return null;
            }

            string pidText = lines[0].TrimEnd('\r');
            string startText = lines[1].TrimEnd('\r');

            if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || pid <= 0) {
                return null;
            }
            if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out long fileTime)) {
                return null;
            }

            DateTime startUtc;
            try {
                startUtc = DateTime.FromFileTimeUtc(fileTime);
            } catch (ArgumentOutOfRangeException) {
                return null;
            }

            return new PidRecord(pid, startUtc);
        }

        private PidFileInspection verify(PidRecord record) {
            try {
                // The RECORDED pid, not our own.
                using (Process process = Process.GetProcessById(record.ProcessId)) {
                    long actual = process.StartTime.ToFileTimeUtc();
                    long diff = Math.Abs(actual - record.StartTimeUtc.ToFileTimeUtc());
                    PidFileStatus status = diff <= _stt
                        ? PidFileStatus.OwnerAlive
                        : PidFileStatus.OwnerGone;
                    return new PidFileInspection(status, record);
                }
            } catch (ArgumentException) {
                // No process with this PID.
                return new PidFileInspection(PidFileStatus.OwnerGone, record);
            } catch (InvalidOperationException) {
                // The process exited while we were querying it.
                return new PidFileInspection(PidFileStatus.OwnerGone, record);
            } catch (Win32Exception) {
                // Access denied: the process exists but we cannot verify it.
                return new PidFileInspection(PidFileStatus.Unverifiable, record);
            } catch (NotSupportedException) {
                return new PidFileInspection(PidFileStatus.Unverifiable, record);
            }
        }

        private void unregister() {
            lock (REGISTRY_GATE) {
                if (OWNERS.TryGetValue(_fPath, out PidFile? owner)
                    && ReferenceEquals(owner, this)) {
                    OWNERS.Remove(_fPath);
                }
            }
        }

        private static (int Pid, DateTime Start) readSelf() {
            using (Process self = Process.GetCurrentProcess()) {
                return (self.Id, self.StartTime.ToUniversalTime());
            }
        }

        private static StringComparer pathComparer() {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        private const int LOCK_EX = 2;
        private const int LOCK_NB = 4;
        private const int LOCK_UN = 8;
        private const int EWOULDBLOCK = 35;

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int flock(IntPtr fileDescriptor, int operation);

        // DirectoryNotFoundException & co. derive from IOException but mean
        // "bad path", not "transient I/O problem". Do not swallow them.
        private static bool isPathProblem(IOException ex) {
            return ex is DirectoryNotFoundException
                || ex is DriveNotFoundException
                || ex is PathTooLongException;
        }

        private void throwIfDisposed() {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PidFile));
            }
        }

        #endregion
    }
}
