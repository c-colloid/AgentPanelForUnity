using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Colloid.AgentPanel.Core.Acp;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// Everything <see cref="UapStdioRelayBuilder"/> needs to compile the
    /// relay, captured up front so the compile itself can run off the
    /// Unity main thread (see that class's doc comment). A non-null
    /// <see cref="FailureReason"/> means resolution already failed and the
    /// builder should not attempt anything else.
    /// </summary>
    public sealed class RelayToolchainPaths
    {
        /// <summary>Absolute path to Editor/Tools~/UapMcpStdioRelay under THIS package's own install (PackageInfo.resolvedPath -- works for an embedded, local, git or registry package alike).</summary>
        public string SourceDir;

        /// <summary>Unity's bundled dotnet host (dotnet[.exe]).</summary>
        public string DotnetHost;

        /// <summary>Unity's bundled Roslyn command-line compiler (csc.dll, run through the dotnet host above).</summary>
        public string CscDll;

        /// <summary>The single highest-versioned Microsoft.NETCore.App shared-framework directory Unity ships.</summary>
        public string SharedFrameworkDir;

        /// <summary>Folder name of <see cref="SharedFrameworkDir"/> (e.g. "8.0.10") -- the exact version relay.runtimeconfig.json pins.</summary>
        public string SharedFrameworkVersion;

        /// <summary>The project's Library/UnityAgentPanel/McpStdioRelay directory (compile output cache root).</summary>
        public string OutputRoot;

        /// <summary>Non-null = resolution failed; every other field above is meaningless.</summary>
        public string FailureReason;
    }

    /// <summary>
    /// Resolves Unity's bundled dotnet host + Roslyn and compiles the
    /// stdio MCP relay (Editor/Tools~/UapMcpStdioRelay/RelayCore.cs +
    /// RelayMain.cs -- invisible to Unity's own Asset Database/compiler
    /// because of the trailing "~", so those two files are NEVER part of
    /// this package's own asmdef) into
    /// Library/UnityAgentPanel/McpStdioRelay/&lt;sourceHash&gt;/relay.dll, once
    /// per content hash. Design note docs/design-notes/2026-09-22-uloop-
    /// phase1.md section 2.7 item 3.
    ///
    /// Path layout learned from (not copied from -- this class is a small
    /// fraction of its size and skips the DotNetSdk/sdk/*/Roslyn/bincore
    /// fallback and the required-runtime-major detection) uLoop 3.6.3's
    /// ExternalCompilerPathResolver: Unity ships its dotnet host at
    /// &lt;root&gt;/NetCoreRuntime/dotnet[.exe], the shared framework at
    /// &lt;root&gt;/NetCoreRuntime/shared/Microsoft.NETCore.App/&lt;version&gt;/, and
    /// Roslyn at &lt;root&gt;/DotNetSdkRoslyn/csc.dll, where &lt;root&gt; is either
    /// EditorApplication.applicationContentsPath directly or (observed on
    /// macOS) &lt;contentsPath&gt;/Resources/Scripting.
    ///
    /// Threading contract (the section 2.7 item 3 brief this class
    /// implements): <see cref="CapturePaths"/> touches
    /// EditorApplication.applicationContentsPath, PackageManager.PackageInfo
    /// .FindForAssembly (which UnityCsReference's own
    /// RenderPipelineResourcesEditorUtils.cs warns "will always provide
    /// null in Worker thread") and Application.dataPath, so it MUST run on
    /// the Unity main thread. Everything below it
    /// (<see cref="TryGetOrBuild"/> and its helpers) is plain file IO +
    /// Process and is safe from ANY thread -- which matters because the
    /// caller, AcpProtocolBridge.BuildMcpServers through
    /// AcpLaunchSpec.McpStdioRelayFactory, runs on the ACP bridge's
    /// background reader thread (AcpProtocolBridge's own class doc), not
    /// the Unity main thread.
    ///
    /// Never throws: every failure is caught, logged, cached by content
    /// hash so a broken toolchain is not re-probed/re-compiled on every
    /// reconnect, and reported as "no relay available" (a null
    /// <see cref="AcpStdioServer"/>) -- a broken toolchain degrades to
    /// today's http-only behavior instead of taking the bridge down.
    /// </summary>
    public static class UapStdioRelayBuilder
    {
        /// <summary>Generous but bounded -- a first-time relay compile is a few small files against an already-resolved reference set; anything longer than this points at a stuck/broken toolchain, not a slow one.</summary>
        internal const int CompileTimeoutMillis = 30000;

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, string> _failureCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// One background build per source hash (see <see cref="Warm"/>).
        /// The result is the built relay.dll path, or null on failure.
        /// </summary>
        private static readonly Dictionary<string, Task<string>> _builds =
            new Dictionary<string, Task<string>>(StringComparer.Ordinal);

        /// <summary>
        /// How long <see cref="TryGetOrBuild"/> waits for a build already
        /// running in the background. The caller is AcpProtocolBridge on the
        /// ACP reader thread, holding AcpBridgeTransport's bridge lock, and
        /// the main thread takes that same lock to send to the agent -- so
        /// a first-time compile (up to <see cref="CompileTimeoutMillis"/>)
        /// must never run there. Warming at spawn gives the build the whole
        /// agent start-up + initialize round trip as a head start; a build
        /// still not done after this wait makes THIS session relay-less
        /// (reported to the user) and the next connection picks up the
        /// cached dll.
        /// </summary>
        internal const int FactoryWaitMillis = 3000;

        /// <summary>Incremented every time <see cref="Compile"/> actually starts a compiler process -- test-only, so a cached-failure test can assert a second call did NOT recompile.</summary>
        internal static int CompileAttemptCountForTests;

        /// <summary>Test-only reset between cases; production code never calls this (the cache is meant to survive the whole editor session).</summary>
        internal static void ClearCachesForTests()
        {
            lock (_cacheLock)
            {
                _failureCache.Clear();
                _builds.Clear();
            }
            CompileAttemptCountForTests = 0;
        }

        /// <summary>
        /// MAIN THREAD ONLY (class doc comment). Resolves everything the
        /// (thread-agnostic) build step below needs, or fills
        /// <see cref="RelayToolchainPaths.FailureReason"/> when Unity's own
        /// bundled toolchain cannot be found -- this method itself never
        /// throws.
        /// </summary>
        public static RelayToolchainPaths CapturePaths()
        {
            string sourceDir;
            try
            {
                // Fully qualified: UnityEditor also has a PackageInfo type, and with both
                // namespaces imported the short name does not compile.
                UnityEditor.PackageManager.PackageInfo info =
                    UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UapStdioRelayBuilder).Assembly);
                if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                {
                    return Failed("Could not resolve this package's own install path"
                        + " (PackageInfo.FindForAssembly returned null -- called off the main thread?).");
                }
                sourceDir = Path.Combine(info.resolvedPath, "Editor", "Tools~", "UapMcpStdioRelay");
            }
            catch (Exception ex)
            {
                return Failed("PackageInfo.FindForAssembly threw: " + ex.Message);
            }

            string outputRoot;
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                outputRoot = Path.Combine(projectRoot, "Library", "UnityAgentPanel", "McpStdioRelay");
            }
            catch (Exception ex)
            {
                return Failed("Could not resolve the project's Library directory: " + ex.Message);
            }

            string contentsPath;
            try
            {
                contentsPath = EditorApplication.applicationContentsPath;
            }
            catch (Exception ex)
            {
                return Failed("EditorApplication.applicationContentsPath threw: " + ex.Message);
            }

            RelayToolchainPaths result = ResolveToolchainFromContentsPath(
                contentsPath, Application.platform == RuntimePlatform.WindowsEditor);
            result.SourceDir = sourceDir;
            result.OutputRoot = outputRoot;
            return result;
        }

        private static RelayToolchainPaths Failed(string reason)
        {
            return new RelayToolchainPaths { FailureReason = reason };
        }

        /// <summary>
        /// Pure file-system probe, no Unity API -- split out from
        /// <see cref="CapturePaths"/> so an EditMode test can point it at a
        /// FAKE directory tree instead of the real Unity install (design
        /// section 2.7 item 6). Tries the contents root directly, then
        /// &lt;root&gt;/Resources/Scripting (the macOS layout observed by
        /// uLoop's own resolver -- see class doc comment).
        /// </summary>
        internal static RelayToolchainPaths ResolveToolchainFromContentsPath(string contentsPath, bool isWindows)
        {
            if (string.IsNullOrEmpty(contentsPath) || !Directory.Exists(contentsPath))
            {
                return Failed("Unity's applicationContentsPath ('" + contentsPath + "') does not exist.");
            }
            string dotnetName = isWindows ? "dotnet.exe" : "dotnet";
            string[] roots = { contentsPath, Path.Combine(contentsPath, "Resources", "Scripting") };
            for (int i = 0; i < roots.Length; i++)
            {
                string root = roots[i];
                string dotnetHost = Path.Combine(root, "NetCoreRuntime", dotnetName);
                string cscDll = Path.Combine(root, "DotNetSdkRoslyn", "csc.dll");
                string sharedRoot = Path.Combine(root, "NetCoreRuntime", "shared", "Microsoft.NETCore.App");
                if (!File.Exists(dotnetHost) || !File.Exists(cscDll) || !Directory.Exists(sharedRoot))
                {
                    continue;
                }
                string frameworkDir = ResolveHighestVersionDirectory(sharedRoot);
                if (frameworkDir == null)
                {
                    continue;
                }
                return new RelayToolchainPaths
                {
                    DotnetHost = dotnetHost,
                    CscDll = cscDll,
                    SharedFrameworkDir = frameworkDir,
                    SharedFrameworkVersion = Path.GetFileName(frameworkDir)
                };
            }
            return Failed("Unity's bundled dotnet host + Roslyn were not found under '" + contentsPath
                + "' (checked NetCoreRuntime + DotNetSdkRoslyn directly, and under Resources/Scripting).");
        }

        /// <summary>Highest-versioned subdirectory of a Microsoft.NETCore.App shared-framework root, or null when none parses as a Version.</summary>
        internal static string ResolveHighestVersionDirectory(string sharedRoot)
        {
            if (!Directory.Exists(sharedRoot))
            {
                return null;
            }
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(sharedRoot);
            }
            catch (Exception)
            {
                return null;
            }
            string best = null;
            Version bestVersion = null;
            for (int i = 0; i < dirs.Length; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                Version v;
                if (Version.TryParse(name, out v) && (bestVersion == null || v > bestVersion))
                {
                    bestVersion = v;
                    best = dirs[i];
                }
            }
            return best;
        }

        /// <summary>
        /// Resolves (compiling once per content hash if needed) and returns
        /// the launch spec for this <paramref name="url"/>, or null when
        /// the toolchain/source/compile is unavailable -- see class doc
        /// comment for the full "never throws" contract. Safe from ANY
        /// thread.
        /// </summary>
        public static AcpStdioServer TryGetOrBuild(RelayToolchainPaths paths, string url, Action<string> log)
        {
            try
            {
                return TryGetOrBuildCore(paths, url, log);
            }
            catch (Exception ex)
            {
                Log(log, "Unexpected failure building the stdio relay: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// MAIN THREAD at spawn (AgentHub): starts the relay build in the
        /// background if it is not already on disk, so the bridge-facing
        /// <see cref="TryGetOrBuild"/> finds it ready instead of compiling
        /// under the bridge lock. Cheap when the dll already exists (a hash
        /// of two small files). Never throws.
        /// </summary>
        public static void Warm(RelayToolchainPaths paths, Action<string> log)
        {
            try
            {
                string hash;
                if (!TryPrepare(paths, log, out hash))
                {
                    return;
                }
                StartOrGetBuild(paths, hash, log);
            }
            catch (Exception ex)
            {
                Log(log, "Could not start the stdio relay build: " + ex.Message);
            }
        }

        private static AcpStdioServer TryGetOrBuildCore(RelayToolchainPaths paths, string url, Action<string> log)
        {
            if (string.IsNullOrEmpty(url))
            {
                Log(log, "No UapOps URL was supplied; nothing to relay to.");
                return null;
            }
            string hash;
            if (!TryPrepare(paths, log, out hash))
            {
                return null;
            }
            Task<string> build = StartOrGetBuild(paths, hash, log);
            if (build == null)
            {
                return null;
            }
            // Bounded wait OUTSIDE _cacheLock (see FactoryWaitMillis for why
            // the caller cannot afford a full compile).
            if (!build.Wait(FactoryWaitMillis))
            {
                Log(log, "Stdio relay is still compiling; Unity operation tools will be available to this"
                    + " agent from the next connection.");
                return null;
            }
            string dllPath = build.Result;
            if (dllPath == null)
            {
                return null;
            }
            return new AcpStdioServer
            {
                Command = paths.DotnetHost,
                Args = new List<string> { "exec", dllPath, url }
            };
        }

        /// <summary>Validates paths and sources and computes the content hash; false (logged) when nothing can be built.</summary>
        private static bool TryPrepare(RelayToolchainPaths paths, Action<string> log, out string hash)
        {
            hash = null;
            if (paths == null || paths.FailureReason != null)
            {
                Log(log, paths == null ? "No toolchain paths captured." : paths.FailureReason);
                return false;
            }
            string relayCorePath = Path.Combine(paths.SourceDir, "RelayCore.cs");
            string relayMainPath = Path.Combine(paths.SourceDir, "RelayMain.cs");
            if (!File.Exists(relayCorePath) || !File.Exists(relayMainPath))
            {
                Log(log, "UapMcpStdioRelay source files are missing under '" + paths.SourceDir + "'.");
                return false;
            }
            try
            {
                hash = ComputeSourceHash(relayCorePath, relayMainPath, paths.SharedFrameworkVersion);
            }
            catch (Exception ex)
            {
                Log(log, "Could not hash the relay sources: " + ex.Message);
                return false;
            }
            lock (_cacheLock)
            {
                string cachedFailure;
                if (_failureCache.TryGetValue(hash, out cachedFailure))
                {
                    Log(log, "Stdio relay: reusing cached failure (" + cachedFailure + ").");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The build for <paramref name="hash"/>: an already-completed one when
        /// the dll is on disk, the one already running, or a new background
        /// one. Two callers never compile the same hash into the same folder
        /// concurrently.
        /// </summary>
        private static Task<string> StartOrGetBuild(RelayToolchainPaths paths, string hash, Action<string> log)
        {
            string outDir = Path.Combine(paths.OutputRoot ?? string.Empty, hash);
            string dllPath = Path.Combine(outDir, "relay.dll");
            string runtimeConfigPath = Path.Combine(outDir, "relay.runtimeconfig.json");
            lock (_cacheLock)
            {
                Task<string> existing;
                if (_builds.TryGetValue(hash, out existing))
                {
                    return existing;
                }
                if (File.Exists(dllPath) && File.Exists(runtimeConfigPath))
                {
                    Task<string> done = Task.FromResult(dllPath);
                    _builds[hash] = done;
                    return done;
                }
                string relayCorePath = Path.Combine(paths.SourceDir, "RelayCore.cs");
                string relayMainPath = Path.Combine(paths.SourceDir, "RelayMain.cs");
                Task<string> build = Task.Run(new Func<string>(delegate
                {
                    string failure;
                    try
                    {
                        failure = Compile(paths, relayCorePath, relayMainPath, outDir, dllPath, runtimeConfigPath);
                    }
                    catch (Exception ex)
                    {
                        failure = ex.Message;
                    }
                    if (failure == null)
                    {
                        return dllPath;
                    }
                    lock (_cacheLock)
                    {
                        _failureCache[hash] = failure;
                        _builds.Remove(hash);
                    }
                    Log(log, "Stdio relay compile failed: " + failure);
                    return (string)null;
                }));
                _builds[hash] = build;
                return build;
            }
        }

        /// <summary>SHA-256 of both source files + the shared-framework version (the version rides the runtimeconfig.json this same build writes, so a Unity update that changes it must recompile too), truncated to 16 hex chars -- plenty for a cache-directory/cache-key name, not a security boundary.</summary>
        internal static string ComputeSourceHash(string relayCorePath, string relayMainPath, string frameworkVersion)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] a = File.ReadAllBytes(relayCorePath);
                byte[] b = File.ReadAllBytes(relayMainPath);
                byte[] v = Encoding.UTF8.GetBytes(frameworkVersion ?? string.Empty);
                byte[] combined = new byte[a.Length + b.Length + v.Length];
                Buffer.BlockCopy(a, 0, combined, 0, a.Length);
                Buffer.BlockCopy(b, 0, combined, a.Length, b.Length);
                Buffer.BlockCopy(v, 0, combined, a.Length + b.Length, v.Length);
                byte[] hash = sha.ComputeHash(combined);
                StringBuilder sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Invokes Unity's bundled csc.dll (through its bundled dotnet
        /// host) via a response file -- avoids command-line length/quoting
        /// issues with long reference lists on Windows. Returns null on
        /// success (dllPath + runtimeConfigPath both written); a failure
        /// description string otherwise. Never throws (every step is
        /// wrapped); safe from any thread (class doc comment).
        /// </summary>
        private static string Compile(RelayToolchainPaths paths, string relayCorePath, string relayMainPath,
            string outDir, string dllPath, string runtimeConfigPath)
        {
            CompileAttemptCountForTests++;
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                return "could not create '" + outDir + "': " + ex.Message;
            }

            string[] frameworkDlls;
            try
            {
                frameworkDlls = Directory.GetFiles(paths.SharedFrameworkDir, "*.dll");
            }
            catch (Exception ex)
            {
                return "could not list shared framework dlls: " + ex.Message;
            }
            if (frameworkDlls.Length == 0)
            {
                return "shared framework directory '" + paths.SharedFrameworkDir + "' has no dlls.";
            }

            StringBuilder rsp = new StringBuilder();
            rsp.Append("/nologo").Append('\n');
            rsp.Append("/noconfig").Append('\n');
            rsp.Append("/target:exe").Append('\n');
            rsp.Append("/platform:anycpu").Append('\n');
            rsp.Append("/main:Colloid.AgentPanel.Tools.McpStdioRelay.RelayMain").Append('\n');
            rsp.Append("/out:").Append(QuoteArg(dllPath)).Append('\n');
            for (int i = 0; i < frameworkDlls.Length; i++)
            {
                rsp.Append("/r:").Append(QuoteArg(frameworkDlls[i])).Append('\n');
            }
            rsp.Append(QuoteArg(relayCorePath)).Append('\n');
            rsp.Append(QuoteArg(relayMainPath)).Append('\n');

            string rspPath = Path.Combine(outDir, "compile.rsp");
            try
            {
                File.WriteAllText(rspPath, rsp.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return "could not write '" + rspPath + "': " + ex.Message;
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = paths.DotnetHost,
                Arguments = QuoteArg(paths.CscDll) + " @" + QuoteArg(rspPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            StringBuilder output = new StringBuilder();
            object outputLock = new object();
            int exitCode = -1;
            bool timedOut = false;
            using (Process process = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler onLine = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null)
                    {
                        return;
                    }
                    lock (outputLock)
                    {
                        output.Append(e.Data).Append('\n');
                    }
                };
                process.OutputDataReceived += onLine;
                process.ErrorDataReceived += onLine;
                try
                {
                    if (!process.Start())
                    {
                        return "dotnet host '" + paths.DotnetHost + "' did not start.";
                    }
                }
                catch (Exception ex)
                {
                    return "could not start dotnet host '" + paths.DotnetHost + "': " + ex.Message;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(CompileTimeoutMillis))
                {
                    timedOut = true;
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                    }
                }
                else
                {
                    process.WaitForExit(2000);
                    try
                    {
                        exitCode = process.ExitCode;
                    }
                    catch (Exception)
                    {
                        exitCode = -1;
                    }
                }
            }
            if (timedOut)
            {
                return "compile timed out after " + CompileTimeoutMillis + "ms.";
            }
            if (exitCode != 0 || !File.Exists(dllPath))
            {
                string text;
                lock (outputLock)
                {
                    text = output.ToString();
                }
                return "csc exited " + exitCode + ": " + Truncate(text, 500);
            }

            // dotnet needs a runtimeconfig.json alongside the compiled
            // assembly naming the exact shared-framework version to bind
            // to -- csc itself never writes one (that is an msbuild/
            // `dotnet publish` step this direct-csc-invocation deliberately
            // skips).
            string tfm = TfmFor(paths.SharedFrameworkVersion);
            string runtimeConfigJson = "{\n  \"runtimeOptions\": {\n    \"tfm\": \"" + tfm
                + "\",\n    \"framework\": {\n      \"name\": \"Microsoft.NETCore.App\",\n      \"version\": \""
                + paths.SharedFrameworkVersion + "\"\n    }\n  }\n}\n";
            try
            {
                File.WriteAllText(runtimeConfigPath, runtimeConfigJson, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return "compiled but could not write '" + runtimeConfigPath + "': " + ex.Message;
            }
            return null;
        }

        internal static string TfmFor(string frameworkVersion)
        {
            Version v;
            if (Version.TryParse(frameworkVersion, out v))
            {
                return "net" + v.Major + "." + v.Minor;
            }
            return "net6.0";
        }

        internal static string QuoteArg(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
            {
                return text ?? string.Empty;
            }
            return text.Substring(0, max) + "...";
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log("[UapStdioRelayBuilder] " + message);
            }
        }
    }
}
