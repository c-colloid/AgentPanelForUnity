using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Colloid.AgentPanel.Core.Json;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// What uap_execute_code was asked to do. Default is Run -- unlike
    /// uap_compile/uap_play_mode (whose default is a read), this tool
    /// exists specifically to run a snippet, and "status"/"reset" are the
    /// exceptions an agent has to spell out.
    /// </summary>
    public enum UapExecuteCodeAction
    {
        Run = 0,
        Status = 1,
        Reset = 2,
    }

    /// <summary>One static-scan finding: an API/pattern that makes the call require confirm:true (roadmap phase 3, section 4.3 -- flags never refuse by themselves).</summary>
    public struct UapExecuteRiskFlag
    {
        /// <summary>Stable machine id, e.g. "process", "network", "io-write". Used by tests and by the permission card to group flags.</summary>
        public string Id;
        /// <summary>Human-readable reason, shown in the preview and the permission card.</summary>
        public string Message;
    }

    /// <summary>The wrapped C# source plus enough bookkeeping to map a compiler/runtime line back to the user's own code.</summary>
    public struct UapExecuteWrappedSource
    {
        /// <summary>The full C# file written to Snippet.cs.</summary>
        public string Source;
        /// <summary>How many lines precede the FIRST line of the user's own code (1-based line numbers subtract this to land on the user's own line 1).</summary>
        public int PreambleLineCount;
        public string ClassName;
        public string Namespace;
    }

    /// <summary>
    /// Unity-free core of uap_execute_code (docs/design-notes/2026-09-28-
    /// uloop-phase3.md, uLoop's execute-dynamic-code): the wrapping, the
    /// content hash that names the cached assembly, line-number remapping,
    /// the static risk scan, and the generic half of the result-shape
    /// rules. No UnityEngine/UnityEditor in sight, so ci/SmokeTests
    /// compiles and runs this file verbatim -- the same split
    /// UapCompilePolicy/UapGameWatchPolicy already use for a Unity-coupled
    /// tool's decision logic. The Unity-side shell (UapExecuteCodeTool)
    /// owns AssemblyBuilder, Assembly.Load, Undo, Debug.Log capture and the
    /// UnityEngine.Object half of the result shape (passed in here as a
    /// delegate so THIS file never references UnityEngine.Object at all).
    /// </summary>
    public static class UapExecuteCodePolicy
    {
        /// <summary>Namespace every wrapped snippet lives in.</summary>
        public const string SnippetNamespace = "UapSnippets";

        /// <summary>Method every wrapped snippet exposes.</summary>
        public const string RunMethodName = "Run";

        /// <summary>
        /// Lines before the user's own code ALWAYS equals this, regardless
        /// of how many usings (default + custom) were requested --
        /// <see cref="BuildWrappedSource"/> packs every `using` onto ONE
        /// line and opens namespace+class+method on a second line, exactly
        /// so this number never depends on per-call input. Because of that,
        /// a compiler message or a caught exception's stack-trace line can
        /// always be mapped back with this one constant, whether the
        /// snippet was just compiled or is being re-run from the cache
        /// (where nothing about THIS call's own usings/code is otherwise
        /// kept around).
        /// </summary>
        public const int PreambleLineCount = 2;

        /// <summary>
        /// Added to every snippet regardless of the `usings` argument (roadmap
        /// section 4.1): the set an agent reaches for by default. Order here
        /// is cosmetic only -- BuildEffectiveUsings sorts the final list so
        /// the hash/wrapped source never depend on argument order.
        /// </summary>
        public static readonly string[] DefaultUsings =
        {
            "System", "System.Linq", "System.Collections.Generic", "UnityEngine", "UnityEditor"
        };

        /// <summary>Sane upper bound on custom usings -- this is an escape hatch, not a place to smuggle a hundred namespaces.</summary>
        public const int MaxUsingsCount = 32;

        /// <summary>Sane upper bound on the code argument's length (characters). Rejected before it ever reaches disk/the compiler.</summary>
        public const int MaxCodeLength = 20000;

        /// <summary>Past this many assemblies loaded in this domain, action:run's reply adds a warning naming action:reset (roadmap section 4.2).</summary>
        public const int LoadedSnippetWarnThreshold = 32;

        /// <summary>IEnumerable return values are capped at this many items (roadmap section 3).</summary>
        public const int MaxReturnArrayItems = 200;

        /// <summary>Debug.Log lines captured during one Run() call, capped by count...</summary>
        public const int MaxLogCapturedLines = 50;

        /// <summary>...and by total characters, whichever is hit first.</summary>
        public const int MaxLogCapturedChars = 4000;

        // -- action -----------------------------------------------------------

        /// <summary>Pure: an absent action is "run" (the tool's default); an unrecognized one is refused BY NAME.</summary>
        public static UapExecuteCodeAction ParseAction(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return UapExecuteCodeAction.Run;
            }
            string trimmed = value.Trim();
            if (string.Equals(trimmed, "run", StringComparison.OrdinalIgnoreCase))
            {
                return UapExecuteCodeAction.Run;
            }
            if (string.Equals(trimmed, "status", StringComparison.OrdinalIgnoreCase))
            {
                return UapExecuteCodeAction.Status;
            }
            if (string.Equals(trimmed, "reset", StringComparison.OrdinalIgnoreCase))
            {
                return UapExecuteCodeAction.Reset;
            }
            throw new ArgumentException("Unknown action '" + value + "'. Valid actions: run, status, reset.");
        }

        public static string ActionName(UapExecuteCodeAction action)
        {
            switch (action)
            {
                case UapExecuteCodeAction.Run: return "run";
                case UapExecuteCodeAction.Status: return "status";
                case UapExecuteCodeAction.Reset: return "reset";
                default: return "run";
            }
        }

        // -- usings -------------------------------------------------------------

        /// <summary>
        /// A dotted C# identifier ("System.Diagnostics", "MyGame.Utils") --
        /// nothing else. Rejecting anything looser matters here: a `using`
        /// entry is spliced directly into the wrapped source as `using
        /// &lt;value&gt;;`, so without this check a crafted entry like
        /// "System; }} public class Evil {{ ... " could break out of the
        /// intended `using` statement and inject arbitrary top-level code
        /// that the static scan (which only ever looks at the `code`
        /// argument) would never see.
        /// </summary>
        private static readonly Regex UsingIdentifierPattern = new Regex(
            "\\A[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*\\z", RegexOptions.Compiled);

        // Strict: no trimming here (a caller passing a raw value gets an
        // exact answer), and \A..\z anchors because $ also matches before a
        // trailing newline. BuildEffectiveUsings trims before it asks.
        public static bool IsValidUsing(string value)
        {
            return !string.IsNullOrEmpty(value) && UsingIdentifierPattern.IsMatch(value);
        }

        /// <summary>
        /// Default usings UNIONED with validated/deduped custom usings, sorted ordinal
        /// (so the wrapped source -- and therefore the hash -- never
        /// depends on the order an agent happened to list them in). Throws
        /// ArgumentException naming the first invalid entry, or when more
        /// than <see cref="MaxUsingsCount"/> custom entries were given.
        /// </summary>
        public static List<string> BuildEffectiveUsings(IEnumerable<string> custom)
        {
            var set = new HashSet<string>(DefaultUsings, StringComparer.Ordinal);
            if (custom != null)
            {
                int count = 0;
                foreach (string entry in custom)
                {
                    count++;
                    if (count > MaxUsingsCount)
                    {
                        throw new ArgumentException(
                            "Too many entries in 'usings' (max " + MaxUsingsCount.ToString(CultureInfo.InvariantCulture) + ").");
                    }
                    if (entry == null || !IsValidUsing(entry.Trim()))
                    {
                        throw new ArgumentException(
                            "Invalid entry in 'usings': '" + (entry ?? "(null)")
                            + "'. Each entry must be a plain dotted namespace, e.g. \"System.Diagnostics\".");
                    }
                    set.Add(entry.Trim());
                }
            }
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // -- hashing --------------------------------------------------------------

        /// <summary>
        /// The exact text SHA-256 is computed over: one using per line
        /// (already sorted/deduped by <see cref="BuildEffectiveUsings"/>),
        /// a delimiter line, then the code argument VERBATIM (no
        /// trim/normalize -- "identical code never loads twice" is a
        /// simplest-possible contract: byte-identical input, byte-identical
        /// hash). Public so a test can pin the exact framing.
        /// </summary>
        public static string NormalizedHashInput(IList<string> effectiveUsings, string code)
        {
            var sb = new StringBuilder();
            if (effectiveUsings != null)
            {
                for (int i = 0; i < effectiveUsings.Count; i++)
                {
                    sb.Append(effectiveUsings[i]).Append('\n');
                }
            }
            sb.Append("--CODE--\n");
            sb.Append(code ?? string.Empty);
            return sb.ToString();
        }

        /// <summary>Lower-case hex SHA-256 of <see cref="NormalizedHashInput"/> -- names the cache entry, the class, the Temp/ folder and the dll.</summary>
        public static string ComputeHash(IList<string> effectiveUsings, string code)
        {
            string input = NormalizedHashInput(effectiveUsings, code);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        public static string ClassName(string hash)
        {
            return "S_" + hash;
        }

        public static string DllFileName(string hash)
        {
            return "UapSnippet_" + hash + ".dll";
        }

        // -- wrapping -------------------------------------------------------------

        /// <summary>
        /// Wraps <paramref name="code"/> into a single-file, uniquely-named
        /// static class (roadmap section 4.1): `public static class
        /// S_&lt;hash&gt; { public static object Run() { &lt;code&gt;
        /// return null; } }`. The trailing `return null;` is what makes "a
        /// trailing expression-less body returns null" true regardless of
        /// what the user wrote -- if every path in `code` already returns,
        /// this line is simply unreachable (a harmless CS0162 warning, not
        /// an error). All wrapper punctuation before the user's code is
        /// packed onto AS FEW LINES AS POSSIBLE so
        /// <see cref="UapExecuteWrappedSource.PreambleLineCount"/> is a
        /// single small, easy-to-verify number: one line of `using`
        /// statements (defaults + as many customs as fit are still one
        /// entry per line below it) followed by one line opening
        /// namespace+class+method together.
        /// </summary>
        public static UapExecuteWrappedSource BuildWrappedSource(IList<string> effectiveUsings, string code, string hash)
        {
            string className = ClassName(hash);
            var sb = new StringBuilder();

            // Line 1: every `using` on one line -- valid C# (semicolons
            // delimit each directive), and keeps the preamble to exactly
            // ONE line no matter how many usings were requested.
            for (int i = 0; i < effectiveUsings.Count; i++)
            {
                sb.Append("using ").Append(effectiveUsings[i]).Append(';');
            }
            sb.Append('\n');

            // Line 2: namespace/class/method open, all on one line.
            sb.Append("namespace ").Append(SnippetNamespace).Append('{')
                .Append("public static class ").Append(className).Append('{')
                .Append("public static object ").Append(RunMethodName).Append("(){")
                .Append('\n');

            // The user's own code starts here, at line (preamble + 1).
            sb.Append(code ?? string.Empty);
            if (code == null || code.Length == 0 || code[code.Length - 1] != '\n')
            {
                sb.Append('\n');
            }
            sb.Append("return null;}}}\n");

            return new UapExecuteWrappedSource
            {
                Source = sb.ToString(),
                PreambleLineCount = PreambleLineCount,
                ClassName = className,
                Namespace = SnippetNamespace,
            };
        }

        /// <summary>
        /// Maps a 1-based line number FROM THE WRAPPED SOURCE (a compiler
        /// message's `line`, or a stack frame's file line) back to the
        /// user's own 1-based line number. 0 means "not inside the user's
        /// code" (the wrapper preamble, or the trailing `return null;}}}`
        /// line) -- callers report that case as-is rather than guessing.
        /// </summary>
        public static int MapCompilerLineToUserLine(int compilerLine, int preambleLineCount)
        {
            int userLine = compilerLine - preambleLineCount;
            return userLine > 0 ? userLine : 0;
        }

        /// <summary>As the 2-arg overload, using the fixed <see cref="PreambleLineCount"/> every wrapped snippet has.</summary>
        public static int MapCompilerLineToUserLine(int compilerLine)
        {
            return MapCompilerLineToUserLine(compilerLine, PreambleLineCount);
        }

        // -- static risk scan -----------------------------------------------------

        /// <summary>
        /// Replaces every comment and string/char literal in <paramref name="code"/>
        /// with spaces (newlines kept, so line numbers of whatever survives
        /// are unchanged) -- so <see cref="ScanForRiskFlags"/> only ever
        /// matches an API name that appears as actual CODE, never one that
        /// merely appears inside a comment ("// don't call Process.Start")
        /// or a string literal ("the word Process.Start"). Handles //
        /// line comments, /* */ block comments, "regular" and @"verbatim"
        /// strings (with their respective escape rules), 'c' char literals,
        /// and $"interpolated"/$@"verbatim interpolated" strings -- for an
        /// interpolated string, only the LITERAL TEXT portions are blanked;
        /// a `{expression}` hole is left untouched and still scanned, since
        /// a call could otherwise hide inside one (`$"{Process.Start(x)}"`).
        /// Not a full C# lexer (a raw string literal `"""..."""`, C# 11, is
        /// not recognized -- this package targets C# 9/LangVersion 9 per
        /// its own floor) -- documented best-effort, same stance as
        /// ScriptGate's own shell-command scanner.
        /// </summary>
        public static string StripCommentsAndStrings(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return code ?? string.Empty;
            }
            var sb = new StringBuilder(code.Length);
            int i = 0;
            int n = code.Length;
            while (i < n)
            {
                char c = code[i];
                char next = i + 1 < n ? code[i + 1] : '\0';

                // Line comment.
                if (c == '/' && next == '/')
                {
                    while (i < n && code[i] != '\n')
                    {
                        sb.Append(' ');
                        i++;
                    }
                    continue;
                }
                // Block comment.
                if (c == '/' && next == '*')
                {
                    sb.Append(' ').Append(' ');
                    i += 2;
                    while (i < n && !(code[i] == '*' && i + 1 < n && code[i + 1] == '/'))
                    {
                        sb.Append(code[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (i < n)
                    {
                        sb.Append(' ').Append(' ');
                        i += 2;
                    }
                    continue;
                }
                // Interpolated string ($"..." / $@"..." / @$"...").
                bool interpolated = c == '$' && (next == '"' || (next == '@' && i + 2 < n && code[i + 2] == '"'));
                bool verbatimInterpolated = c == '$' && next == '@';
                if (!interpolated && c == '@' && next == '$')
                {
                    interpolated = true;
                    verbatimInterpolated = true;
                }
                if (interpolated)
                {
                    int openLen = verbatimInterpolated ? 3 : 2; // "$@\"" or "$\""
                    sb.Append(' ', Math.Min(openLen, n - i));
                    i += openLen;
                    StripInterpolatedBody(code, ref i, n, verbatimInterpolated, sb);
                    continue;
                }
                // Verbatim string (@"...", "" is an escaped quote).
                if (c == '@' && next == '"')
                {
                    sb.Append(' ').Append(' ');
                    i += 2;
                    while (i < n)
                    {
                        if (code[i] == '"')
                        {
                            if (i + 1 < n && code[i + 1] == '"')
                            {
                                sb.Append(' ').Append(' ');
                                i += 2;
                                continue;
                            }
                            sb.Append(' ');
                            i++;
                            break;
                        }
                        sb.Append(code[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }
                // Regular string ("...", \" escapes).
                if (c == '"')
                {
                    sb.Append(' ');
                    i++;
                    while (i < n && code[i] != '"' && code[i] != '\n')
                    {
                        if (code[i] == '\\' && i + 1 < n)
                        {
                            sb.Append(' ').Append(' ');
                            i += 2;
                            continue;
                        }
                        sb.Append(' ');
                        i++;
                    }
                    if (i < n && code[i] == '"')
                    {
                        sb.Append(' ');
                        i++;
                    }
                    continue;
                }
                // Char literal ('c', '\'', '\\').
                if (c == '\'')
                {
                    int start = i;
                    sb.Append(' ');
                    i++;
                    if (i < n && code[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(' ').Append(' ');
                        i += 2;
                    }
                    else if (i < n && code[i] != '\'')
                    {
                        sb.Append(' ');
                        i++;
                    }
                    if (i < n && code[i] == '\'')
                    {
                        sb.Append(' ');
                        i++;
                    }
                    else
                    {
                        // Did not look like a char literal after all (e.g. a
                        // generic type parameter apostrophe never occurs in
                        // C#, but be defensive): rewind to just past the
                        // opening quote we already blanked, so we do not
                        // eat the rest of the file.
                        i = start + 1;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Blanks the literal-text runs of an interpolated string starting
        /// right after its opening quote, leaving `{expr}` holes
        /// unblanked (recursing is not needed: braces are simply not
        /// stripped, so the outer scan's own comment/string handling
        /// applies to whatever is inside them on the next pass over the
        /// same buffer -- here we just need to find where the interpolated
        /// string ENDS). `{{`/`}}` are escaped literal braces, not holes.
        /// </summary>
        private static void StripInterpolatedBody(string code, ref int i, int n, bool verbatim, StringBuilder sb)
        {
            int depth = 0;
            while (i < n)
            {
                char c = code[i];
                if (depth == 0)
                {
                    if (c == '"' && (!verbatim || i + 1 >= n || code[i + 1] != '"'))
                    {
                        sb.Append(' ');
                        i++;
                        return;
                    }
                    if (c == '"' && verbatim && i + 1 < n && code[i + 1] == '"')
                    {
                        sb.Append(' ').Append(' ');
                        i += 2;
                        continue;
                    }
                    if (!verbatim && c == '\\' && i + 1 < n)
                    {
                        sb.Append(' ').Append(' ');
                        i += 2;
                        continue;
                    }
                    if (c == '{' && i + 1 < n && code[i + 1] == '{')
                    {
                        sb.Append(' ').Append(' ');
                        i += 2;
                        continue;
                    }
                    if (c == '}' && i + 1 < n && code[i + 1] == '}')
                    {
                        sb.Append(' ').Append(' ');
                        i += 2;
                        continue;
                    }
                    if (c == '{')
                    {
                        depth = 1;
                        sb.Append(c);
                        i++;
                        continue;
                    }
                    sb.Append(c == '\n' ? '\n' : ' ');
                    i++;
                    continue;
                }
                // Inside a {expression} hole: copy verbatim (still scanned
                // for risk flags later) and just track brace depth,
                // including nested string/char literals so a '}' inside a
                // string does not end the hole early.
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }
                sb.Append(c);
                i++;
            }
        }

        private struct RiskRule
        {
            public string Id;
            public Regex Pattern;
            public string Message;
        }

        private static readonly RiskRule[] RiskRules =
        {
            new RiskRule
            {
                Id = "process",
                Pattern = new Regex(@"\b(Process|ProcessStartInfo)\b", RegexOptions.Compiled),
                Message = "Starts an external process (System.Diagnostics.Process/ProcessStartInfo)."
            },
            new RiskRule
            {
                Id = "network",
                Pattern = new Regex(
                    @"\b(HttpClient|WebClient|Socket|TcpClient|TcpListener|UdpClient|NetworkStream)\b"
                    + @"|\bSystem\s*\.\s*Net\b|\bDns\s*\.\s*GetHostAddresses\b",
                    RegexOptions.Compiled),
                Message = "Makes a network call (System.Net / HttpClient / WebClient / Socket / TcpClient / UdpClient / Dns)."
            },
            new RiskRule
            {
                Id = "io-write",
                Pattern = new Regex(
                    @"\bFile\s*\.\s*(Write\w*|Copy|Move|Create\w*|Delete|Replace|AppendAllText|AppendAllLines|AppendText|Open\w*)\b"
                    + @"|\bDirectory\s*\.\s*(CreateDirectory|Delete|Move)\b|\bFileInfo\b|\bFileStream\b",
                    RegexOptions.Compiled),
                Message = "Writes or deletes files/directories (File/Directory/FileInfo/FileStream)."
            },
            new RiskRule
            {
                Id = "reflection",
                Pattern = new Regex(
                    @"\bType\s*\.\s*GetType\b|\bActivator\s*\.\s*CreateInstance\b|\.\s*InvokeMember\s*\("
                    + @"|\bBindingFlags\b",
                    RegexOptions.Compiled),
                Message = "Resolves a type or member by name at runtime (Type.GetType/Activator.CreateInstance/"
                    + "InvokeMember/BindingFlags) -- this can reach an API none of the other rules recognize by"
                    + " name (e.g. Type.GetType(\"System.Diagnostics.\" + \"Process\")); the static scan is a"
                    + " mitigation, not a sandbox, and reflection is the clearest way around it."
            },
            new RiskRule
            {
                Id = "env",
                Pattern = new Regex(@"\bEnvironment\s*\.\s*(SetEnvironmentVariable|Exit)\b", RegexOptions.Compiled),
                Message = "Changes process environment or exits the process (Environment.SetEnvironmentVariable/Exit)."
            },
            new RiskRule
            {
                Id = "editor-exit",
                Pattern = new Regex(@"\bEditorApplication\s*\.\s*Exit\b", RegexOptions.Compiled),
                Message = "Quits the Unity Editor (EditorApplication.Exit)."
            },
            new RiskRule
            {
                Id = "asset-delete",
                Pattern = new Regex(
                    @"\bAssetDatabase\s*\.\s*(DeleteAsset|MoveAssetToTrash)\b|\bFileUtil\s*\.\s*DeleteFileOrDirectory\b",
                    RegexOptions.Compiled),
                Message = "Deletes an asset (AssetDatabase.DeleteAsset/MoveAssetToTrash or FileUtil.DeleteFileOrDirectory)."
            },
            new RiskRule
            {
                Id = "prefs-clear",
                Pattern = new Regex(@"\b(EditorPrefs|PlayerPrefs)\s*\.\s*DeleteAll\b", RegexOptions.Compiled),
                Message = "Clears every EditorPrefs/PlayerPrefs entry (DeleteAll)."
            },
            new RiskRule
            {
                Id = "dynamic-assembly",
                Pattern = new Regex(@"\bAssembly\s*\.\s*Load\w*\b|\bAppDomain\b", RegexOptions.Compiled),
                Message = "Loads another assembly or touches AppDomain (Assembly.Load*/AppDomain)."
            },
            new RiskRule
            {
                Id = "native-interop",
                Pattern = new Regex(@"\bDllImport\b|\bextern\b", RegexOptions.Compiled),
                Message = "Declares native interop (DllImport/extern)."
            },
            new RiskRule
            {
                Id = "unsafe",
                Pattern = new Regex(@"\bunsafe\b", RegexOptions.Compiled),
                Message = "Uses unsafe code (snippets are compiled without /unsafe, so it will not compile even with confirm:true)."
            },
            new RiskRule
            {
                Id = "sleep",
                Pattern = new Regex(@"\bThread\s*\.\s*Sleep\b", RegexOptions.Compiled),
                Message = "Blocks the Unity main thread (Thread.Sleep) -- no runtime limit can stop it (see the tool's description)."
            },
        };

        /// <summary>
        /// A `while(true)`/`for(;;)` with no `break`/`return`/`throw`
        /// ANYWHERE in the code is flagged as a likely infinite loop.
        /// Deliberately coarse (roadmap section 4.3's "obvious infinite
        /// loops"): this is a code-wide co-occurrence check, not a
        /// per-loop reachability analysis -- a `break` inside an unrelated
        /// second loop still suppresses the flag. Precise loop-body
        /// matching would need a real parser, which this scan intentionally
        /// is not (same stance as ScriptGate.ExtractBashWriteTargets).
        /// </summary>
        private static readonly Regex InfiniteLoopShape = new Regex(
            @"\bwhile\s*\(\s*true\s*\)|\bfor\s*\(\s*;\s*;\s*\)", RegexOptions.Compiled);

        private static readonly Regex EscapeWord = new Regex(@"\b(break|return|throw)\b", RegexOptions.Compiled);

        /// <summary>
        /// Runs every rule over <paramref name="code"/> with comments and
        /// string/char literals stripped first (see
        /// <see cref="StripCommentsAndStrings"/>). Absolute-path detection
        /// ("any IO call with an absolute path outside the project root")
        /// is intentionally NOT attempted here: telling an absolute path
        /// apart from a project-relative one needs the project root, which
        /// this Unity-free file has no notion of -- the io-write rule above
        /// already flags every File/Directory WRITE call regardless of
        /// path, which is the safe superset (roadmap section 4.3 lists the
        /// absolute-path case as "when detectable"; this scan detects the
        /// call, not the path).
        /// </summary>
        public static List<UapExecuteRiskFlag> ScanForRiskFlags(string code)
        {
            var result = new List<UapExecuteRiskFlag>();
            string stripped = StripCommentsAndStrings(code ?? string.Empty);
            for (int i = 0; i < RiskRules.Length; i++)
            {
                if (RiskRules[i].Pattern.IsMatch(stripped))
                {
                    result.Add(new UapExecuteRiskFlag { Id = RiskRules[i].Id, Message = RiskRules[i].Message });
                }
            }
            if (InfiniteLoopShape.IsMatch(stripped) && !EscapeWord.IsMatch(stripped))
            {
                result.Add(new UapExecuteRiskFlag
                {
                    Id = "infinite-loop",
                    Message = "Looks like an unconditional loop (while(true)/for(;;)) with no break/return/throw"
                        + " anywhere in the snippet -- likely hangs the Editor with no way to stop it."
                });
            }
            return result;
        }

        // -- result shape -----------------------------------------------------

        /// <summary>
        /// Supplied by the Unity-side tool: true (with name/type/path
        /// filled in) when <paramref name="value"/> is a UnityEngine.Object
        /// worth describing specially; false for anything else (this file
        /// never references UnityEngine.Object directly).
        /// </summary>
        public delegate bool TryDescribeUnityObject(object value, out string name, out string typeName, out string path);

        /// <summary>
        /// Converts a snippet's `Run()` return value to JSON per roadmap
        /// section 3's rules: null; primitives/strings/enums as
        /// themselves; a UnityEngine.Object (per <paramref name="describeUnityObject"/>)
        /// as {name, type, path}; an IEnumerable that is not a string as
        /// {"items":[...], "count":N, "truncated":bool} (capped at
        /// <see cref="MaxReturnArrayItems"/> -- wrapped in an object rather
        /// than a bare array so the truncated flag has somewhere to live);
        /// anything else (including Vector/Color/Quaternion/Rect, which
        /// <paramref name="describeUnityObject"/> never claims) via
        /// ToString(). A ToString() that itself throws degrades to the
        /// exception's Message rather than propagating (the RETURN VALUE
        /// shape must never be what makes an otherwise-successful run
        /// fail).
        /// </summary>
        public static JsonNode ShapeReturnValue(object value, TryDescribeUnityObject describeUnityObject)
        {
            if (value == null)
            {
                return JsonNode.Null;
            }
            if (value is bool b)
            {
                return JsonNode.Of(b);
            }
            if (value is string s)
            {
                return JsonNode.Of(s);
            }
            if (value is char ch)
            {
                return JsonNode.Of(ch.ToString());
            }
            if (IsIntegral(value))
            {
                return JsonNode.Of(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }
            if (IsFloating(value))
            {
                return JsonNode.Of(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }
            if (value.GetType().IsEnum)
            {
                return JsonNode.Of(value.ToString());
            }
            string name, typeName, path;
            if (describeUnityObject != null && describeUnityObject(value, out name, out typeName, out path))
            {
                return JsonNode.NewObject()
                    .Set("name", name ?? string.Empty)
                    .Set("type", typeName ?? string.Empty)
                    .Set("path", path ?? string.Empty);
            }
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                // Bounded on purpose: an infinite (or merely huge) generator
                // must never be fully drained just to COUNT it -- this loop
                // stops reading the source enumerable the instant it knows
                // the answer is "truncated", so it never enumerates more
                // than MaxReturnArrayItems + 1 items no matter how long (or
                // endless) the snippet's sequence actually is.
                JsonNode items = JsonNode.NewArray();
                bool truncated = false;
                IEnumerator it = enumerable.GetEnumerator();
                try
                {
                    while (it.MoveNext())
                    {
                        if (items.Count >= MaxReturnArrayItems)
                        {
                            truncated = true;
                            break;
                        }
                        items.Add(ShapeReturnValue(it.Current, describeUnityObject));
                    }
                }
                finally
                {
                    (it as IDisposable)?.Dispose();
                }
                return JsonNode.NewObject()
                    .Set("items", items)
                    .Set("count", items.Count)
                    .Set("truncated", truncated);
            }
            return JsonNode.Of(SafeToString(value));
        }

        private static bool IsIntegral(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong;
        }

        private static bool IsFloating(object value)
        {
            return value is float || value is double || value is decimal;
        }

        private static string SafeToString(object value)
        {
            try
            {
                return value.ToString();
            }
            catch (Exception ex)
            {
                return "(ToString() threw: " + ex.Message + ")";
            }
        }

        // -- memory warning -----------------------------------------------------

        /// <summary>Empty when under threshold; otherwise the exact wording every reply uses (pinned by a test).</summary>
        public static string DescribeMemoryWarning(int loadedCount)
        {
            if (loadedCount <= LoadedSnippetWarnThreshold)
            {
                return string.Empty;
            }
            return loadedCount.ToString(CultureInfo.InvariantCulture) + " snippet assemblies are loaded in this"
                + " Editor session and cannot be unloaded until the next domain reload (Assembly.Load(byte[]) is"
                + " permanent per domain). Call uap_execute_code action:reset (confirm:true) to request a script"
                + " reload and free them.";
        }
    }
}
