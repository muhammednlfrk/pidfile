using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace PidFile.NET.Tests {
    // IMPORTANT (Unix): while a PidFile in THIS process owns a path, never
    // open and close that path yourself (File.ReadAllText, File.Exists is
    // fine). Closing any descriptor of the file drops the process's fcntl
    // lock. Tests read the record through Inspect() or from another process.
    public sealed class PidFileTests : IDisposable {
        private const string HELPER_DLL = "PidFile.NET.Tests.Helper.dll";
        private const string HELPER_READY = "READY";
        private const string HELPER_BUSY = "BUSY";
        private const int HELPER_BUSY_EXIT_CODE = 2;
        private const int PROCESS_TIMEOUT_MS = 15000;

        private readonly string _dir;
        private readonly string _path;
        private readonly List<Process> _helpers = new List<Process>();

        public PidFileTests() {
            // xunit creates a new instance per test: every test gets its own
            // directory, so tests can run in parallel.
            _dir = Path.Combine(
                Path.GetTempPath(),
                "pidfile-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "app.pid");
        }

        public void Dispose() {
            foreach (Process helper in _helpers) {
                try {
                    if (!helper.HasExited) {
                        helper.Kill();
                        helper.WaitForExit(PROCESS_TIMEOUT_MS);
                    }
                } catch (InvalidOperationException) {
                    // Already gone.
                } finally {
                    helper.Dispose();
                }
            }

            try {
                Directory.Delete(_dir, recursive: true);
            } catch (IOException) {
                // Best effort.
            } catch (UnauthorizedAccessException) {
                // Best effort.
            }
        }

        // ---------------------------------------------------------------
        // Constructor
        // ---------------------------------------------------------------

        [Fact]
        public void Ctor_NullPath_Throws() {
            Assert.Throws<ArgumentException>(() => new PidFile(null!));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Ctor_EmptyOrWhitespacePath_Throws(string path) {
            Assert.Throws<ArgumentException>(() => new PidFile(path));
        }

        [Fact]
        public void Ctor_RelativePath_Throws() {
            Assert.Throws<ArgumentException>(() => new PidFile("relative/app.pid"));
        }

        [Fact]
        public void Ctor_NegativeTolerance_Throws() {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PidFile(_path, TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void Ctor_InjectedNonPositivePid_Throws() {
            ConstructorInfo? ctor = typeof(PidFile).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(string), typeof(int), typeof(DateTime), typeof(TimeSpan?) },
                modifiers: null);

            TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(
                () => ctor!.Invoke(new object?[] { _path, 0, DateTime.UtcNow, null }));
            Assert.IsType<ArgumentOutOfRangeException>(invocation.InnerException);
        }

        [Fact]
        public void Ctor_InjectedNonUtcStartTime_Throws() {
            TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(
                () => injected(_path, 1, DateTime.Now));
            Assert.IsType<ArgumentException>(invocation.InnerException);
        }

        [Fact]
        public void Properties_DescribeCurrentProcess() {
            (int pid, DateTime startUtc, long _) = identity();

            using (PidFile pidFile = new PidFile(_path)) {
                Assert.Equal(_path, pidFile.FilePath);
                Assert.Equal(pid, pidFile.Record.ProcessId);
                Assert.True(
                    Math.Abs((pidFile.Record.StartTimeUtc - startUtc).TotalSeconds) < 2.0,
                    "Record start time should match this process.");
            }
        }

        // ---------------------------------------------------------------
        // Single process: acquire / release / dispose
        // ---------------------------------------------------------------

        [Fact]
        public void Acquire_OwnsFile_AndRecordVerifiesAsAlive() {
            using (PidFile pidFile = new PidFile(_path)) {
                Assert.True(pidFile.TryAcquire());

                PidFileInspection result = pidFile.Inspect();
                Assert.Equal(PidFileStatus.OwnerAlive, result.Status);
                Assert.Equal(identity().Pid, result.Record!.Value.ProcessId);
            }
        }

        [Fact]
        public void Acquire_SecondInstanceSamePath_ReturnsFalse() {
            using (PidFile first = new PidFile(_path))
            using (PidFile second = new PidFile(_path)) {
                Assert.True(first.TryAcquire());
                Assert.False(second.TryAcquire());
            }
        }

        [Fact]
        public void Acquire_Twice_OnSameInstance_Throws() {
            using (PidFile pidFile = new PidFile(_path)) {
                Assert.True(pidFile.TryAcquire());
                Assert.Throws<InvalidOperationException>(() => pidFile.TryAcquire());
            }
        }

        [Fact]
        public void Acquire_MissingDirectory_ThrowsInsteadOfReturningFalse() {
            string badPath = Path.Combine(_dir, "does-not-exist", "app.pid");
            using (PidFile pidFile = new PidFile(badPath)) {
                // "Bad path" must not be confused with "someone else owns it".
                Assert.Throws<DirectoryNotFoundException>(() => pidFile.TryAcquire());
            }
        }

        [Fact]
        public void Acquire_AfterFailedAcquire_CanBeRetried() {
            string dir = Path.Combine(_dir, "later");
            string path = Path.Combine(dir, "app.pid");

            using (PidFile pidFile = new PidFile(path)) {
                Assert.Throws<DirectoryNotFoundException>(() => pidFile.TryAcquire());

                // A failed acquire must not leave the path registered.
                Directory.CreateDirectory(dir);
                Assert.True(pidFile.TryAcquire());
            }
        }

        [Fact]
        public void Acquire_OverwritesStaleRecord() {
            writeRecordFile(99999, 1);

            using (PidFile pidFile = new PidFile(_path)) {
                Assert.True(pidFile.TryAcquire());

                PidFileInspection result = pidFile.Inspect();
                Assert.Equal(PidFileStatus.OwnerAlive, result.Status);
                Assert.Equal(identity().Pid, result.Record!.Value.ProcessId);
            }
        }

        [Fact]
        public void Acquire_WithInjectedStaleIdentity_RecordFailsVerification() {
            (int pid, DateTime startUtc, long _) = identity();
            DateTime wrongStart = startUtc - TimeSpan.FromHours(1);

            using (PidFile pidFile = injected(_path, pid, wrongStart)) {
                Assert.True(pidFile.TryAcquire());

                PidFileInspection result = pidFile.Inspect();
                Assert.Equal(PidFileStatus.OwnerGone, result.Status);
                Assert.Equal(pid, result.Record!.Value.ProcessId);
            }
        }

        [Fact]
        public void Release_ClearsRecord_ButKeepsFile() {
            using (PidFile pidFile = new PidFile(_path)) {
                Assert.True(pidFile.TryAcquire());
                Assert.True(pidFile.TryRelease());

                // The file stays (deleting it would race with new owners);
                // only the record is cleared.
                Assert.True(File.Exists(_path));
                Assert.Equal(PidFileStatus.NoRecord, pidFile.Inspect().Status);
            }
        }

        [Fact]
        public void Release_WithoutAcquire_ReturnsFalse() {
            using (PidFile pidFile = new PidFile(_path)) {
                Assert.False(pidFile.TryRelease());
            }
        }

        [Fact]
        public void Release_Twice_SecondReturnsFalse() {
            using (PidFile pidFile = new PidFile(_path)) {
                Assert.True(pidFile.TryAcquire());
                Assert.True(pidFile.TryRelease());
                Assert.False(pidFile.TryRelease());
            }
        }

        [Fact]
        public void Release_AllowsAnotherInstanceToAcquire() {
            using (PidFile first = new PidFile(_path))
            using (PidFile second = new PidFile(_path)) {
                Assert.True(first.TryAcquire());
                Assert.True(first.TryRelease());
                Assert.True(second.TryAcquire());
            }
        }

        [Fact]
        public void Release_ThenAcquireAgain_OnSameInstance_Works() {
            using (PidFile pidFile = new PidFile(_path)) {
                Assert.True(pidFile.TryAcquire());
                Assert.True(pidFile.TryRelease());
                Assert.True(pidFile.TryAcquire());
            }
        }

        [Fact]
        public void Dispose_ReleasesOwnership() {
            PidFile first = new PidFile(_path);
            Assert.True(first.TryAcquire());
            first.Dispose();

            using (PidFile second = new PidFile(_path)) {
                Assert.True(second.TryAcquire());
            }
        }

        [Fact]
        public void Dispose_IsIdempotent() {
            PidFile pidFile = new PidFile(_path);
            Assert.True(pidFile.TryAcquire());

            pidFile.Dispose();
            pidFile.Dispose();
        }

        [Fact]
        public void Dispose_WithoutAcquire_DoesNotThrow() {
            PidFile pidFile = new PidFile(_path);
            pidFile.Dispose();
        }

        [Fact]
        public void MembersAfterDispose_ThrowObjectDisposed() {
            PidFile pidFile = new PidFile(_path);
            pidFile.Dispose();

            Assert.Throws<ObjectDisposedException>(() => pidFile.TryAcquire());
            Assert.Throws<ObjectDisposedException>(() => pidFile.TryRelease());
            Assert.Throws<ObjectDisposedException>(() => pidFile.Inspect());
        }

        // ---------------------------------------------------------------
        // Inspect: statuses
        // ---------------------------------------------------------------

        [Fact]
        public void Inspect_MissingFile_ReturnsNoRecord() {
            using (PidFile pidFile = new PidFile(_path)) {
                PidFileInspection result = pidFile.Inspect();

                Assert.Equal(PidFileStatus.NoRecord, result.Status);
                Assert.Null(result.Record);
            }
        }

        [Fact]
        public void Inspect_MissingDirectory_Throws() {
            string badPath = Path.Combine(_dir, "does-not-exist", "app.pid");
            using (PidFile pidFile = new PidFile(badPath)) {
                Assert.Throws<DirectoryNotFoundException>(() => pidFile.Inspect());
            }
        }

        [Fact]
        public void Inspect_EmptyFile_ReturnsNoRecord() {
            File.WriteAllText(_path, string.Empty);

            using (PidFile pidFile = new PidFile(_path)) {
                Assert.Equal(PidFileStatus.NoRecord, pidFile.Inspect().Status);
            }
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("\n")]
        [InlineData("1")]
        [InlineData("abc\n1\n")]
        [InlineData("1\nabc\n")]
        [InlineData("-5\n1\n")]
        [InlineData("0\n1\n")]
        [InlineData("1\n-1\n")]
        [InlineData("1\n9223372036854775807\n")]
        [InlineData("1\n99999999999999999999\n")]
        [InlineData("1\n2\n3\n")]
        public void Inspect_MalformedRecord_ReturnsMalformed(string content) {
            File.WriteAllText(_path, content);

            using (PidFile pidFile = new PidFile(_path)) {
                PidFileInspection result = pidFile.Inspect();

                Assert.Equal(PidFileStatus.Malformed, result.Status);
                Assert.Null(result.Record);
            }
        }

        [Fact]
        public void Inspect_OversizedFile_ReturnsMalformed() {
            File.WriteAllText(_path, new string('1', 4096));

            using (PidFile pidFile = new PidFile(_path)) {
                Assert.Equal(PidFileStatus.Malformed, pidFile.Inspect().Status);
            }
        }

        [Fact]
        public void Inspect_CrLfLineEndings_AreAccepted() {
            (int pid, DateTime _, long fileTime) = identity();
            File.WriteAllText(
                _path,
                pid.ToString(CultureInfo.InvariantCulture)
                    + "\r\n"
                    + fileTime.ToString(CultureInfo.InvariantCulture)
                    + "\r\n");

            using (PidFile pidFile = new PidFile(_path)) {
                Assert.Equal(PidFileStatus.OwnerAlive, pidFile.Inspect().Status);
            }
        }

        [Fact]
        public void Inspect_MissingTrailingNewline_IsAccepted() {
            (int pid, DateTime _, long fileTime) = identity();
            File.WriteAllText(
                _path,
                pid.ToString(CultureInfo.InvariantCulture)
                    + "\n"
                    + fileTime.ToString(CultureInfo.InvariantCulture));

            using (PidFile pidFile = new PidFile(_path)) {
                Assert.Equal(PidFileStatus.OwnerAlive, pidFile.Inspect().Status);
            }
        }

        [Fact]
        public void Inspect_PidReuse_SamePidDifferentStartTime_ReturnsOwnerGone() {
            // The scenario this library exists for: the recorded PID is alive
            // right now (this test process) but belongs to a different
            // process than the one that wrote the record.
            (int pid, DateTime _, long fileTime) = identity();
            writeRecordFile(pid, fileTime - TimeSpan.FromHours(1).Ticks);

            using (PidFile pidFile = new PidFile(_path)) {
                PidFileInspection result = pidFile.Inspect();

                Assert.Equal(PidFileStatus.OwnerGone, result.Status);
                Assert.Equal(pid, result.Record!.Value.ProcessId);
            }
        }

        [Fact]
        public void Inspect_NonExistentPid_ReturnsOwnerGone() {
            writeRecordFile(int.MaxValue, 1);

            using (PidFile pidFile = new PidFile(_path)) {
                PidFileInspection result = pidFile.Inspect();

                Assert.Equal(PidFileStatus.OwnerGone, result.Status);
                Assert.Equal(int.MaxValue, result.Record!.Value.ProcessId);
            }
        }

        [Theory]
        [InlineData(1, true)]    // inside the default 2 s tolerance
        [InlineData(-1, true)]
        [InlineData(5, false)]   // outside it
        [InlineData(-5, false)]
        public void Inspect_RespectsDefaultStartTimeTolerance(int offsetSeconds, bool expectedAlive) {
            (int pid, DateTime _, long fileTime) = identity();
            writeRecordFile(pid, fileTime + (offsetSeconds * TimeSpan.TicksPerSecond));

            using (PidFile pidFile = new PidFile(_path)) {
                PidFileStatus expected = expectedAlive
                    ? PidFileStatus.OwnerAlive
                    : PidFileStatus.OwnerGone;
                Assert.Equal(expected, pidFile.Inspect().Status);
            }
        }

        [Fact]
        public void Inspect_WiderCustomTolerance_AcceptsLargerOffset() {
            (int pid, DateTime _, long fileTime) = identity();
            writeRecordFile(pid, fileTime + (5 * TimeSpan.TicksPerSecond));

            using (PidFile pidFile = new PidFile(_path, TimeSpan.FromSeconds(10))) {
                Assert.Equal(PidFileStatus.OwnerAlive, pidFile.Inspect().Status);
            }
        }

        [Fact]
        public void Inspect_NarrowerCustomTolerance_RejectsSmallerOffset() {
            (int pid, DateTime _, long fileTime) = identity();
            writeRecordFile(pid, fileTime + (1 * TimeSpan.TicksPerSecond));

            using (PidFile pidFile = new PidFile(_path, TimeSpan.FromMilliseconds(200))) {
                Assert.Equal(PidFileStatus.OwnerGone, pidFile.Inspect().Status);
            }
        }

        // ---------------------------------------------------------------
        // Cross-process
        // ---------------------------------------------------------------

        [Fact]
        public void CrossProcess_LiveOwner_BlocksAcquire_AndIsVerifiedAlive() {
            Process helper = startHelper(_path);

            using (PidFile observer = new PidFile(_path)) {
                Assert.False(observer.TryAcquire());

                PidFileInspection result = observer.Inspect();
                Assert.Equal(PidFileStatus.OwnerAlive, result.Status);
                Assert.Equal(helper.Id, result.Record!.Value.ProcessId);
            }
        }

        [Fact]
        public void CrossProcess_RecordIsReadableWhileOwnerHoldsTheLock() {
            Process helper = startHelper(_path);

            // The single-file design's core claim: the record stays readable
            // while the lock is held (this is why the lock sits on a byte far
            // beyond the record).
            string[] lines = readRecordText().Split('\n');

            Assert.Equal(3, lines.Length);
            Assert.Equal(helper.Id.ToString(CultureInfo.InvariantCulture), lines[0]);
            Assert.True(
                long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out long _),
                "Second line should be the start time as a number.");
            Assert.Equal(string.Empty, lines[2]);
        }

        [Fact]
        public void CrossProcess_KilledOwner_RecordIsStale_AndLockIsFree() {
            Process helper = startHelper(_path);
            int helperPid = helper.Id;

            // No graceful shutdown: Dispose never runs, like a crash.
            helper.Kill();
            Assert.True(helper.WaitForExit(PROCESS_TIMEOUT_MS));

            using (PidFile pidFile = new PidFile(_path)) {
                // The stale record is still on disk but fails verification.
                PidFileInspection stale = pidFile.Inspect();
                Assert.Equal(PidFileStatus.OwnerGone, stale.Status);
                Assert.Equal(helperPid, stale.Record!.Value.ProcessId);

                // The kernel released the lock, so we can take over.
                Assert.True(pidFile.TryAcquire());
                Assert.Equal(PidFileStatus.OwnerAlive, pidFile.Inspect().Status);
            }
        }

        [Fact]
        public void CrossProcess_GracefulExit_ClearsRecord_AndReleasesLock() {
            Process helper = startHelper(_path);

            // Closing stdin makes the helper leave its using block.
            helper.StandardInput.Close();
            Assert.True(helper.WaitForExit(PROCESS_TIMEOUT_MS));
            Assert.Equal(0, helper.ExitCode);

            Assert.True(File.Exists(_path));
            using (PidFile pidFile = new PidFile(_path)) {
                Assert.Equal(PidFileStatus.NoRecord, pidFile.Inspect().Status);
                Assert.True(pidFile.TryAcquire());
            }
        }

        [Fact]
        public void CrossProcess_SecondHelper_ReportsBusy() {
            startHelper(_path);

            Process second = launchHelper(_path);
            Assert.Equal(HELPER_BUSY, second.StandardOutput.ReadLine());
            Assert.True(second.WaitForExit(PROCESS_TIMEOUT_MS));
            Assert.Equal(HELPER_BUSY_EXIT_CODE, second.ExitCode);
        }

        [Fact]
        public void CrossProcess_OwnerInThisProcess_BlocksHelper() {
            using (PidFile owner = new PidFile(_path)) {
                Assert.True(owner.TryAcquire());

                Process helper = launchHelper(_path);
                Assert.Equal(HELPER_BUSY, helper.StandardOutput.ReadLine());
                Assert.True(helper.WaitForExit(PROCESS_TIMEOUT_MS));
            }
        }

        [Fact]
        public void Inspect_FromAnotherInstanceInSameProcess_DoesNotDropOwnersLock() {
            using (PidFile owner = new PidFile(_path))
            using (PidFile observer = new PidFile(_path)) {
                Assert.True(owner.TryAcquire());

                // Regression guard for the POSIX trap: a naive Inspect would
                // open and close its own descriptor, which silently drops the
                // owner's fcntl lock on Unix.
                Assert.Equal(PidFileStatus.OwnerAlive, observer.Inspect().Status);

                Process helper = launchHelper(_path);
                Assert.Equal(HELPER_BUSY, helper.StandardOutput.ReadLine());
                Assert.True(helper.WaitForExit(PROCESS_TIMEOUT_MS));
            }
        }

        [Fact]
        public void Acquire_FailedAcquireInThisProcess_DoesNotDropOtherOwnersLock() {
            using (PidFile owner = new PidFile(_path))
            using (PidFile loser = new PidFile(_path)) {
                Assert.True(owner.TryAcquire());
                Assert.False(loser.TryAcquire());

                Process helper = launchHelper(_path);
                Assert.Equal(HELPER_BUSY, helper.StandardOutput.ReadLine());
                Assert.True(helper.WaitForExit(PROCESS_TIMEOUT_MS));
            }
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static (int Pid, DateTime StartUtc, long StartFileTime) identity() {
            using (Process self = Process.GetCurrentProcess()) {
                DateTime startUtc = self.StartTime.ToUniversalTime();
                return (self.Id, startUtc, startUtc.ToFileTimeUtc());
            }
        }

        private static PidFile injected(string path, int pid, DateTime startTimeUtc) {
            ConstructorInfo? ctor = typeof(PidFile).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(string), typeof(int), typeof(DateTime), typeof(TimeSpan?) },
                modifiers: null);

            return (PidFile)ctor!.Invoke(new object?[] { path, pid, startTimeUtc, null });
        }

        private void writeRecordFile(int pid, long fileTime) {
            // Only for paths NOT owned by this process (see the note at the
            // top of the class).
            File.WriteAllText(
                _path,
                pid.ToString(CultureInfo.InvariantCulture)
                    + "\n"
                    + fileTime.ToString(CultureInfo.InvariantCulture)
                    + "\n");
        }

        private string readRecordText() {
            // Share mode matters on Windows: the owner has the file open for
            // writing, so a plain File.ReadAllText would be refused.
            using (FileStream fs = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs)) {
                return sr.ReadToEnd();
            }
        }

        private Process launchHelper(string path) {
            string helperDll = Path.Combine(AppContext.BaseDirectory, HELPER_DLL);
            Assert.True(File.Exists(helperDll), "Helper not found next to the tests: " + helperDll);

            // `dotnet test` sets DOTNET_HOST_PATH; otherwise rely on PATH.
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

            ProcessStartInfo info = new ProcessStartInfo {
                FileName = host,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add(helperDll);
            info.ArgumentList.Add(path);

            Process? process = Process.Start(info);
            if (process == null) {
                throw new InvalidOperationException("The helper process could not be started.");
            }

            _helpers.Add(process);
            return process;
        }

        private Process startHelper(string path) {
            Process process = launchHelper(path);

            Task<string?> firstLine = process.StandardOutput.ReadLineAsync();
            if (!firstLine.Wait(PROCESS_TIMEOUT_MS)) {
                throw new TimeoutException("The helper did not report in time.");
            }
            if (firstLine.Result != HELPER_READY) {
                throw new InvalidOperationException(
                    "The helper reported: " + (firstLine.Result ?? "<nothing>"));
            }
            return process;
        }
    }
}
