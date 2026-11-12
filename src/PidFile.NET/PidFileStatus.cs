namespace PidFile.NET {
    /// <summary>
    /// Represents the status of a PID file after inspection.
    /// </summary>
    public enum PidFileStatus {
        /// <summary>
        /// No PID file or record was found at the specified path.
        /// </summary>
        NoRecord,

        /// <summary>
        /// The PID file exists but is malformed or cannot be parsed correctly.
        /// </summary>
        Malformed,

        /// <summary>
        /// Process ID and start time are valid and the process is currently
        /// running.
        /// </summary>
        OwnerAlive,

        /// <summary>
        /// No process with the specified PID is currently running, or the start
        /// time does not match.
        /// </summary>
        OwnerGone,

        /// <summary>
        /// The PID file exists, but the process information cannot be verified
        /// due to insufficient permissions or other reasons.
        /// </summary>
        Unverifiable
    }
}
