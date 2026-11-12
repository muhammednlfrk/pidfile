using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PidFile.NET {
    /// <summary>
    /// Defines the interface for a PID file, which includes methods for
    /// acquiring and releasing the lock, as well as inspecting the PID file's
    /// status.
    /// </summary>
    public interface IPidFile : IDisposable {
        /// <summary>
        /// Gets the file path of the PID file. This property provides the
        /// location of the PID file on the filesystem.
        /// </summary>
        string FilePath { get; }

        /// <summary>
        /// Gets the PID record associated with the PID file.
        /// </summary>
        PidRecord Record { get; }

        /// <summary>
        /// Attempts to acquire the lock on the PID file. If the lock is
        /// successfully acquired, the method returns <c>true</c>; otherwise, it
        /// returns <c>false</c>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the lock is acquired; otherwise, <c>false</c>.
        /// </returns>
        bool TryAcquire();

        /// <summary>
        /// Attempts to release the lock on the PID file. If the lock is
        /// successfully released, the method returns <c>true</c>; otherwise, it
        /// returns <c>false</c>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the lock is released; otherwise, <c>false</c>.
        /// </returns>
        bool TryRelease();

        /// <summary>
        /// Inspects the PID file and returns a <see cref="PidFileInspection"/>
        /// object that contains the status of the PID file and an optional PID
        /// record if the file is valid. This method allows you to check the
        /// status of the PID file without acquiring the lock.
        /// </summary>
        /// <returns>
        /// A <see cref="PidFileInspection"/> object containing the status of the
        /// PID file and an optional PID record if the file is valid.
        /// </returns>
        PidFileInspection Inspect();
    }

    public sealed class PidFile : IPidFile {
        // Default start time tolerance in milliseconds (2 seconds)
        private const long DEFAULTSTT = 2000;

        // Default synchronization behavior (flush to disk)
        private const bool DEFAULTSYNC = true; // Flush to disk by default

        // PID record size in bytes (4 + 8 = 12 bytes)
        private const int RECSIZE = sizeof(int) + sizeof(long);

        // Fields
        private readonly object _lock = new object();
        private readonly string _fPath;  // Process ID (PID) file path
        private readonly PidRecord _rec; // Record associated with the PID file
        private readonly int _pid;       // The records process ID (PID)
        private readonly DateTime _pst;  // The records start time in UTC
        private readonly long _stt;      // Start time tolerance for the checks
        private readonly bool _sync;     // Whether to flush to disk or not

        private bool _disposed;  // Disposal check flag
        private FileStream? _fs; // File stream for the PID file

        /// <summary>
        /// Initializes a new instance of the <see cref="PidFile"/> class with
        /// the specified parameters.
        /// </summary>
        /// <param name="filePath">
        /// The path to the PID file.
        /// </param>
        /// <param name="processId">
        /// The process ID (PID) associated with the PID file.
        /// </param>
        /// <param name="startTimeUtc">
        /// The start time of the process in UTC.
        /// </param>
        /// <param name="startTimeTolerance">
        /// The tolerance for the start time check.
        /// </param>
        /// <param name="sync">
        /// Whether to flush to disk or not.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the file path is null, empty, or whitespace.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the process ID is not a positive integer or the start
        /// time tolerance is negative.
        /// </exception>
        public PidFile(
            string filePath,
            int processId,
            DateTime startTimeUtc,
            TimeSpan? startTimeTolerance = null,
            bool sync = DEFAULTSYNC) {
            if (string.IsNullOrWhiteSpace(filePath)) {
                throw new ArgumentException(
                    "File path cannot be null or whitespace.",
                    nameof(filePath));
            }
            if (!Path.IsPathFullyQualified(filePath.AsSpan())) {
                throw new ArgumentException(
                    "File path must be an absolute path.",
                    nameof(filePath));
            }

            if (processId <= 0) {
                throw new ArgumentOutOfRangeException(
                    nameof(processId),
                    "Process ID must be a positive integer.");
            }

            if (startTimeUtc.Kind != DateTimeKind.Utc) {
                throw new ArgumentException(
                    "Start time must be in UTC.",
                    nameof(startTimeUtc));
            }

            TimeSpan stt = startTimeTolerance ?? TimeSpan.FromMilliseconds(DEFAULTSTT);
            if (stt < TimeSpan.Zero) {
                throw new ArgumentOutOfRangeException(
                    nameof(startTimeTolerance),
                    "Start time tolerance must be non-negative.");
            }

            checked {
                _fPath = Path.GetFullPath(filePath);
                _pid = processId;
                _pst = startTimeUtc;
                _stt = (long)stt.TotalMilliseconds;
                _sync = sync;
            }

            _rec = new PidRecord(_pid, _pst);
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

                FileStream? fs = null;
                bool aqq = false;
                try {
                    // Attempt to open the PID file with write exclusive access
                    // This only ensures that the file is created and locked for
                    // writing for only cooperating processes on UNIX-like systems.
                    // On Windows, this will also prevent other processes from
                    // opening the file for writing.
                    fs = new FileStream(
                        path: _fPath,                        // PID file path
                        mode: FileMode.OpenOrCreate,         // Create the file if it doesn't exist
                        access: FileAccess.ReadWrite,        // RW access
                        share: FileShare.Read,               // Allow other processes to read the file
                        bufferSize: RECSIZE,                 // Buffer size for the PID record
                        options: FileOptions.DeleteOnClose); // Ensure the file is deleted when closed

                    writeRecord(fs);
                    _fs = fs;
                    aqq = true;
                } catch (Exception ex) when (!isPathProblem(ex)) {
                    aqq = false;
                } finally {
                    if (!aqq) {
                        fs?.Dispose();
                    }
                }
                return aqq;
            }
        }

        /// <inheritdoc/>
        public PidFileInspection Inspect() {
            try {
                using (FileStream fs = new FileStream(
                    path: _fPath,
                    mode: FileMode.Open,
                    access: FileAccess.Read,
                    share: FileShare.ReadWrite,
                    bufferSize: RECSIZE,
                    options: FileOptions.SequentialScan)) {
                    if (fs.Length == 0) {
                        return new PidFileInspection(
                            status: PidFileStatus.NoRecord,
                            record: null);
                    } else if (fs.Length != RECSIZE) {
                        return new PidFileInspection(
                            status: PidFileStatus.Malformed,
                            record: null);
                    }

                    PidRecord? record = readRecord(fs);
                    if (record == null) {
                        return new PidFileInspection(
                            status: PidFileStatus.Malformed,
                            record: null);
                    }

                    using (Process p = Process.GetProcessById(_pid)) {
                        long pstTicks = p.StartTime.ToUniversalTime().ToFileTimeUtc();
                        long diff = Math.Abs(pstTicks - record.Value.StartTimeUtc.ToFileTimeUtc());
                        PidFileStatus stat = diff <= _stt ? PidFileStatus.OwnerAlive : PidFileStatus.OwnerGone;
                        return new PidFileInspection(
                            status: stat,
                            record: record);
                    }
                }
            } catch (Exception ex) when ((ex is IOException && (!isPathProblem(ex)))
                || (ex is Win32Exception)
                || (ex is UnauthorizedAccessException)
                || (ex is System.Security.SecurityException)
                || (ex is NotSupportedException)) {
                return new PidFileInspection(PidFileStatus.Unverifiable, null);
            }
        }

        /// <inheritdoc/>
        public bool TryRelease() {
            lock (_lock) {
                throwIfDisposed();
                if (_fs == null) {
                    return false;
                }

                try {
                    _fs.SetLength(0);
                    _fs.Flush(_sync);
                    _fs.Dispose();
                    _fs = null;
                    return true;
                } catch (Exception ex) when (!isPathProblem(ex)) {
                    return false;
                }
            }
        }


        #endregion

        #region Private Methods

        private void writeRecord(FileStream fs) {
            // Create a buffer to hold the PID record data
            byte[] buffer = new byte[RECSIZE];
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, sizeof(int)), _pid);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(sizeof(int), sizeof(long)), _pst.ToFileTimeUtc());

            try {
                // Lock the file, seek to the beginning, write the buffer, set the
                // length, and flush if required
                fs.Lock(0, RECSIZE);
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(buffer, 0, RECSIZE);
                fs.SetLength(RECSIZE);
                fs.Flush(_sync);
            } finally {
                // Ensure the file is unlocked even if an exception occurs
                fs.Unlock(0, RECSIZE);
            }
        }

        private static PidRecord? readRecord(FileStream fs) {
            try {
                byte[] buff = new byte[RECSIZE];
                fs.Seek(0, SeekOrigin.Begin);
                int count = fs.Read(buff, 0, RECSIZE);
                if (count != RECSIZE) {
                    throw new InvalidDataException(
                        $"Expected to read {RECSIZE} bytes, but only read {count} bytes.");
                }
                int pid = BinaryPrimitives.ReadInt32LittleEndian(buff.AsSpan(0, sizeof(int)));
                long pstTicks = BinaryPrimitives.ReadInt64LittleEndian(buff.AsSpan(sizeof(int), sizeof(long)));
                DateTime pst = DateTime.FromFileTimeUtc(pstTicks);
                return new PidRecord(pid, pst);
            } catch {
                return null;
            }
        }

        private static bool isPathProblem(Exception ex) {
            return ex is DirectoryNotFoundException
                || ex is DriveNotFoundException
                || ex is PathTooLongException;
        }

        #endregion

        #region IDisposable Implementation

        private void Dispose(bool disposing) {
            lock (_lock) {
                if (_disposed) {
                    return;
                }

                if (disposing) {
                    TryRelease();
                }

                _fs?.Dispose();
                _fs = null;
                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="PidFile"/> class. This
        /// method is called by the garbage collector when the object is no
        /// longer in use.
        /// </summary>
        ~PidFile() {
            Dispose(disposing: false);
        }

        public void Dispose() {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void throwIfDisposed() {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(PidFile));
            }
        }

        #endregion
    }
}
