using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Colloid.AgentPanel.Core.FileIo
{
    /// <summary>
    /// The ONE atomic text-file write/read/classify helper (MODEL-2), shared
    /// by every persistence sidecar (SessionCacheFile, SessionMetaFile,
    /// QuickActionStore, CustomInstructionsFile, AgentDefinitionFileWriter)
    /// and the Ops generated files (GateHookInstaller, UapOpsMcpConfig).
    /// Before this class the tmp+Replace dance was copy-pasted five times,
    /// and every copy shared the same defect: the cross-filesystem fallback
    /// was `File.Delete(target)` then `File.Move(tmp, target)`, whose
    /// comment claimed "still leaves either the old or the new complete
    /// file" -- FALSE in the window between the two calls, where a crash
    /// loses BOTH (the old is deleted, the new is stranded as `.tmp` that
    /// no reader ever looks at).
    ///
    /// The fallback here renames the old file ASIDE (`.bak`) instead of
    /// deleting it, and <see cref="ReadAllText"/> restores that backup when
    /// the primary is missing -- so every crash window now leaves a
    /// readable generation:
    ///
    /// - crash before the backup rename: old file intact.
    /// - crash between rename and move: primary missing, but `.bak` (old)
    ///   is restored by the next read. Only the latest save is lost.
    /// - crash after the move: new file intact (a stale `.bak` is cleaned
    ///   by the next write, and a lingering one is never restored over an
    ///   existing primary).
    ///
    /// <see cref="IsCorruption"/> is the load-side half (MODEL-1): the
    /// sidecars' "any failure deletes the file" recovery treated a
    /// TRANSIENT IO error (cloud-sync/antivirus lock, permissions hiccup)
    /// the same as real corruption and permanently deleted the user's
    /// pins/renames/groups over a lock that would have cleared a second
    /// later. Callers now delete only when the failure class says the
    /// CONTENT is bad (parse/shape), and keep the file otherwise. Unknown
    /// exception types count as NOT corruption -- the data-safe default
    /// (the load still returns null for this attempt; nothing is wedged).
    ///
    /// <see cref="ReadAllText"/> is share-tolerant (MODEL-3, design note
    /// 2026-09-12-session-cache-transient-read-failure.md): it opens with
    /// FileShare.ReadWrite | FileShare.Delete and retries an IO failure a
    /// few times, because the write half of this very class provokes the
    /// read half's most common failure -- the antivirus/indexer/cloud-sync
    /// agent that opens every freshly replaced file makes a plain
    /// File.ReadAllText (FileShare.Read) fail with "Sharing violation" for
    /// a few milliseconds afterwards.
    ///
    /// Pure System.IO + System.Text (no Unity), so it compiles in the
    /// license-free smoke harness (ci/SmokeTests) alongside its Ops
    /// consumers and is unit tested there and in EditMode against real
    /// temp directories -- the same convention as the classes it serves.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>Suffix of the in-flight write staging file.</summary>
        public const string TmpSuffix = ".tmp";

        /// <summary>Suffix the fallback path parks the previous generation under.</summary>
        public const string BackupSuffix = ".bak";

        /// <summary>
        /// Attempts one <see cref="ReadAllText"/> makes before letting the
        /// IO failure through (MODEL-3). Four attempts with the step below
        /// spend at most 15+30+45 = 90 ms of the calling (main) thread --
        /// enough for a scanner's exclusive window, short enough that a
        /// genuinely wedged file never feels like a hang.
        /// </summary>
        private const int ReadAttempts = 4;

        /// <summary>Base backoff between read attempts; multiplied by the attempt number.</summary>
        private const int ReadRetryStepMs = 15;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Atomic write, throwing variant: creates the parent directory,
        /// stages to `path + ".tmp"` (UTF-8, no BOM), then swaps it in via
        /// File.Replace, falling back to rename-aside + move (see the class
        /// comment for the crash-window analysis) when Replace is not
        /// supported by the filesystem. Callers that must not throw use
        /// <see cref="WriteAllText"/> instead.
        /// </summary>
        public static void WriteAllTextOrThrow(string path, string content)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("path is required", "path");
            }
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string tmpPath = path + TmpSuffix;
            string bakPath = path + BackupSuffix;
            File.WriteAllText(tmpPath, content ?? string.Empty, Utf8NoBom);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmpPath, path, null);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceViaBackup(path, tmpPath, bakPath);
                }
                catch (IOException)
                {
                    // File.Replace can refuse on filesystems without the
                    // needed support (network shares, exFAT, some Linux
                    // mounts) even though tmp is always in the same
                    // directory as the target.
                    ReplaceViaBackup(path, tmpPath, bakPath);
                }
            }
            else
            {
                File.Move(tmpPath, path);
            }
            // A backup from a previous fallback (or an interrupted one whose
            // primary DID land) is now one generation stale -- drop it so
            // ReadAllText can never resurrect it over fresher data.
            TryDeleteRaw(bakPath);
        }

        private static void ReplaceViaBackup(string path, string tmpPath, string bakPath)
        {
            TryDeleteRaw(bakPath);
            File.Move(path, bakPath);
            File.Move(tmpPath, path);
        }

        /// <summary>
        /// Never-throw atomic write. Failures log one line through
        /// <paramref name="log"/> and return false.
        /// </summary>
        public static bool WriteAllText(string path, string content, Action<string> log = null)
        {
            try
            {
                WriteAllTextOrThrow(path, content);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("Atomic write to '" + path + "' failed: " + ex.Message);
                }
                return false;
            }
        }

        /// <summary>
        /// Never-throw atomic write that skips the disk entirely when the
        /// current content already equals <paramref name="content"/>
        /// (ordinal) -- the mtime-preserving behavior GateHookInstaller and
        /// UapOpsMcpConfig rely on for their regenerate-before-every-spawn
        /// files. Read-back uses the same UTF-8 as the write, so the
        /// compare never spuriously reports "changed".
        /// </summary>
        public static bool WriteAllTextIfChanged(string path, string content, Action<string> log = null)
        {
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path, Utf8NoBom);
                    if (string.Equals(existing, content, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable current content just means "write it fresh".
            }
            return WriteAllText(path, content, log);
        }

        /// <summary>
        /// Reads the file, or null when neither it nor a fallback backup
        /// exists (the normal "nothing recorded yet"). When the primary is
        /// missing but a `.bak` from an interrupted
        /// <see cref="WriteAllTextOrThrow"/> fallback exists, the backup is
        /// restored to the primary path first (best effort -- when the
        /// rename fails the backup is read in place), so a crash inside the
        /// write fallback costs at most the latest save, never the whole
        /// file. IO failures THROW so the caller can classify them with
        /// <see cref="IsCorruption"/> -- never-throw wrapping belongs to
        /// the caller's load path, which owns the delete-vs-keep decision.
        /// </summary>
        public static string ReadAllText(string path, Action<string> log = null)
        {
            if (File.Exists(path))
            {
                return ReadShared(path);
            }
            string bakPath = path + BackupSuffix;
            if (!File.Exists(bakPath))
            {
                return null;
            }
            if (log != null)
            {
                log("Restoring '" + path + "' from its interrupted-write backup.");
            }
            try
            {
                File.Move(bakPath, path);
                return ReadShared(path);
            }
            catch (Exception)
            {
                // Restore raced or failed; try the backup in place before
                // giving up (an IO failure here throws to the caller, same
                // as a primary-read failure).
                return File.Exists(bakPath) ? ReadShared(bakPath)
                    : File.Exists(path) ? ReadShared(path)
                    : null;
            }
        }

        /// <summary>
        /// MODEL-3 (design note 2026-09-12-session-cache-transient-read-
        /// failure.md): the read every caller above goes through.
        ///
        /// `File.ReadAllText` opens with FileShare.Read, which on Windows
        /// refuses -- "Sharing violation" -- whenever another handle already
        /// holds the file with WRITE access, even though that handle is
        /// perfectly happy to let us read. Something holds exactly such a
        /// handle, routinely, in the milliseconds after this class's own
        /// File.Replace lands: the antivirus / search-indexer / cloud-sync
        /// agent that scans every freshly written file. Measured in the
        /// wild (Editor.log, one Unity session): six failures, one per
        /// domain reload, every one of them on SessionCache.json right
        /// after the pre-reload save rewrote it.
        ///
        /// FileShare.ReadWrite | FileShare.Delete makes our open COMPATIBLE
        /// with such a handle rather than racing it, which removes the
        /// failure outright; the short bounded retry then covers the
        /// narrower window where the other party denies sharing altogether
        /// (a scanner that has the file open exclusively mid-scan). A
        /// vanished file is never retried -- it is not a lock, and the
        /// budget is main-thread time. Anything still failing after that
        /// throws exactly as before, so <see cref="IsCorruption"/> keeps
        /// classifying real failures for the caller.
        /// </summary>
        private static string ReadShared(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (IOException ex)
                {
                    if (attempt >= ReadAttempts
                        || ex is FileNotFoundException
                        || ex is DirectoryNotFoundException)
                    {
                        throw;
                    }
                    Thread.Sleep(ReadRetryStepMs * attempt);
                }
            }
        }

        /// <summary>
        /// MODEL-1 classification: true when <paramref name="ex"/> says the
        /// file's CONTENT is bad (delete-and-rebuild is the right recovery),
        /// false when the failure is environmental/transient (keep the file;
        /// this attempt just returns nothing). IOException and its
        /// subclasses, plus permission failures, are transient by
        /// definition here -- a cloud-sync or antivirus lock clears on its
        /// own, and deleting a merely-locked sidecar was exactly the
        /// data-loss bug. Unknown exception types default to NOT corruption
        /// (the data-safe side).
        /// </summary>
        public static bool IsCorruption(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }
            if (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
            return ex is Colloid.AgentPanel.Core.Json.JsonParseException
                || ex is InvalidDataException
                || ex is FormatException
                || ex is OverflowException
                || ex is DecoderFallbackException;
        }

        /// <summary>
        /// Best-effort delete of the file AND its stale `.tmp` staging twin.
        /// The `.bak` twin is deliberately KEPT: when a caller deletes a
        /// corrupt primary, a surviving backup is the previous -- possibly
        /// healthy -- generation, and the next <see cref="ReadAllText"/>
        /// restores it (if it is corrupt too, that read's caller deletes it
        /// through this same path and the chain terminates).
        /// </summary>
        public static void TryDelete(string path, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                File.Delete(path);
                TryDeleteRaw(path + TmpSuffix);
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("Failed to delete '" + path + "': " + ex.Message);
                }
            }
        }

        private static void TryDeleteRaw(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort only.
            }
        }
    }
}
