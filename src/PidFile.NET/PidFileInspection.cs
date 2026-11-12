namespace PidFile.NET {
    /// <summary>
    /// Represents the result of inspecting a PID file, including its status and
    /// an optional record if the file is valid.
    /// </summary>
    /// <remarks>
    /// The <see cref="PidFileInspection"/> struct is used to represent the result of inspecting a PID file.
    /// It contains the status of the inspection and an optional PID record if the file is valid.
    /// </remarks>
    public readonly struct PidFileInspection {
        /// <summary>
        /// The status of the PID file inspection, indicating whether the PID
        /// file is valid, malformed, or if the process is alive or gone.
        /// </summary>
        public PidFileStatus Status { get; }

        /// <summary>
        /// The PID record extracted from the PID file, if available. This will
        /// be null if the PID file is malformed or if no record could be read.
        /// </summary>
        public PidRecord? Record { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PidFileInspection"/> struct
        /// </summary>
        /// <param name="status">
        /// The status of the PID file inspection.
        /// </param>
        /// <param name="record">
        /// The PID record extracted from the PID file, if available.
        /// </param>
        public PidFileInspection(PidFileStatus status, PidRecord? record) {
            Status = status;
            Record = record;
        }
    }
}
