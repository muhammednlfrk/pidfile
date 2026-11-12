using System;

namespace PidFile.NET {
    /// <summary>
    /// Represents a PID file record containing the process ID and process start
    /// time.
    /// </summary>
    public readonly struct PidRecord {
        /// <summary>
        /// The process ID (PID).
        /// </summary>
        public int ProcessId { get; }

        /// <summary>
        /// The start time of the process in UTC.
        /// </summary>
        public DateTime StartTimeUtc { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PidRecord"/> struct
        /// with the specified PID and start time.
        /// </summary>
        /// <param name="pid">
        /// The process ID (PID) of the process.
        /// </param>
        /// <param name="startTimeUtc">
        /// The start time of the process in UTC.
        /// </param>
        public PidRecord(int pid, DateTime startTimeUtc) {
            ProcessId = pid;
            StartTimeUtc = startTimeUtc;
        }
    }
}
