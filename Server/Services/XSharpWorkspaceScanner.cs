using LanguageService.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using XSharp.Parser;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Background scanner that walks the workspace root at server startup and
    /// populates <see cref="XSharpWorkspaceIndex"/> from every XSharp source file found.
    /// <para>
    /// Triggered once from <c>Program.cs</c> inside the <c>OnInitialized</c> callback,
    /// immediately after the workspace root is known.  The scan runs on the thread pool
    /// so the LSP handshake is never blocked.
    /// </para>
    /// <para>
    /// Concurrency is capped at <c>ProcessorCount / 2</c> (minimum 1) via a
    /// <see cref="SemaphoreSlim"/> so the scan does not starve LSP request processing.
    /// </para>
    /// </summary>
    public sealed class XSharpWorkspaceScanner
    {
        private readonly ILogger<XSharpWorkspaceScanner> _logger;
        private readonly XSharpConfigurationService _configService;
        private readonly XSharpWorkspaceIndex _index;

        private static readonly string[] SourceExtensions = { "*.prg", "*.prgx", "*.xs", "*.xh", "*.ch" };

        public XSharpWorkspaceScanner(
            ILogger<XSharpWorkspaceScanner> logger,
            XSharpConfigurationService configService,
            XSharpWorkspaceIndex index)
        {
            _logger        = logger;
            _configService = configService;
            _index         = index;
        }

        /// <summary>
        /// Fires a background scan of <paramref name="rootPath"/> and returns immediately.
        /// Safe to call from the LSP <c>initialized</c> callback.
        /// </summary>
        public void StartScan(string rootPath)
        {
            _ = Task.Run(() => ScanAsync(rootPath));
        }

        // ====================================================================
        // Background work
        // ====================================================================

        private async Task ScanAsync(string rootPath)
        {
            _logger.LogInformation("WorkspaceScanner: starting background scan of {Root}", rootPath);

            // Load <Compile Remove> exclusion patterns from all .xsproj files.
            var excludePatterns = LoadExcludePatterns(rootPath);

            // Apply project-level settings derived from the .xsproj (dialect,
            // include paths, standard defs) as defaults for any field the user
            // has not explicitly set via workspace/didChangeConfiguration.
            ApplyProjectSettings(rootPath);

            var files = SourceExtensions
                .SelectMany(ext => Directory.EnumerateFiles(
                    rootPath, ext, SearchOption.AllDirectories))
                .Where(f => !IsOutputDirectory(f))
                .Where(f => !IsExcluded(f, rootPath, excludePatterns))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation(
                "WorkspaceScanner: found {Count} source file(s) to index ({Excl} exclusion pattern(s) from .xsproj)",
                files.Count, excludePatterns.Count);

            // Snapshot parse options once for the whole scan so all files use the
            // same dialect/includes even if settings arrive mid-scan.
            var options  = _configService.GetParseOptions();
            var semaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));

            var tasks = files.Select(async filePath =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try   { IndexFile(filePath, options); }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            _logger.LogInformation(
                "WorkspaceScanner: complete — {Files} file(s), {Symbols} symbol(s), {Tokens} token location(s) indexed",
                _index.IndexedFileCount, _index.IndexedSymbolCount, _index.IndexedTokenCount);
        }

        private void IndexFile(string filePath, LanguageService.CodeAnalysis.XSharp.XSharpParseOptions options)
        {
            try
            {
                var text          = File.ReadAllText(filePath);
                var errorListener = new NullErrorListener();

                bool ok = VsParser.Parse(
                    text,
                    filePath,
                    options,
                    errorListener,
                    out var tokenStream,
                    out var tree,
                    out _);

                if (!ok || tree == null) return;

                var symbols = IndexSymbolExtractor.Extract(tree, filePath, text);
                _index.UpdateFile(filePath, symbols);

                if (tokenStream != null)
                {
                    var tokens = IndexSymbolExtractor.ExtractIdentifiers(tokenStream, filePath);
                    _index.UpdateFileTokens(filePath, tokens);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WorkspaceScanner: failed to index {File}", filePath);
            }
        }

        // ====================================================================
        // Inner types
        // ====================================================================

        // ====================================================================
        // .xsproj project-settings helpers
        // ====================================================================

        /// <summary>
        /// Reads the first <c>.xsproj</c> found under <paramref name="rootPath"/>,
        /// extracts <c>&lt;StandardDefs&gt;</c>, <c>&lt;Dialect&gt;</c>, and
        /// <c>&lt;IncludePaths&gt;</c>, then merges them into the config service
        /// as defaults — only overwriting fields the user has not already set via
        /// <c>workspace/didChangeConfiguration</c>.
        /// </summary>
        private void ApplyProjectSettings(string rootPath)
        {
            try
            {
                var proj = Directory.EnumerateFiles(rootPath, "*.xsproj",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (proj == null) return;

                var projDir = (Path.GetDirectoryName(proj) ?? rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                var doc = XDocument.Load(proj);
                string? Prop(string name) =>
                    doc.Descendants()
                       .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                       ?.Value?.Trim();

                // Resolve $(ProjectDir) / $(projectdir) MSBuild variable.
                string Resolve(string? v) =>
                    string.IsNullOrEmpty(v) ? "" :
                    Regex.Replace(v, @"\$\(projectdir\)", projDir,
                        RegexOptions.IgnoreCase).Trim();

                string projStdDefs     = Resolve(Prop("StandardDefs"));
                string projDialect     = Resolve(Prop("Dialect"));
                string projIncludes    = Resolve(Prop("IncludePaths"));

                if (string.IsNullOrEmpty(projStdDefs) && string.IsNullOrEmpty(projDialect)
                    && string.IsNullOrEmpty(projIncludes))
                    return;

                var current = _configService.GetSettings();

                // Only fill in fields the user has not already configured.
                bool dialectOverride  = !string.IsNullOrEmpty(projDialect)
                    && (string.IsNullOrEmpty(current.Dialect) || current.Dialect == "Core");
                bool includesOverride = !string.IsNullOrEmpty(projIncludes)
                    && string.IsNullOrEmpty(current.IncludePaths);
                bool stdDefsOverride  = !string.IsNullOrEmpty(projStdDefs)
                    && string.IsNullOrEmpty(current.StandardDefs);

                if (!dialectOverride && !includesOverride && !stdDefsOverride) return;

                var merged = new XSharpLanguageServer.Models.XSharpWorkspaceSettings
                {
                    Dialect             = dialectOverride  ? projDialect  : current.Dialect,
                    IncludePaths        = includesOverride ? projIncludes : current.IncludePaths,
                    StandardDefs        = stdDefsOverride  ? projStdDefs  : current.StandardDefs,
                    PreprocessorSymbols = current.PreprocessorSymbols,
                    SemanticDiagnostics  = current.SemanticDiagnostics,
                    WarnOnUndefinedCalls = current.WarnOnUndefinedCalls,
                    IndentCaseLabel         = current.IndentCaseLabel,
                    IndentCaseContent       = current.IndentCaseContent,
                    IndentBlockContent      = current.IndentBlockContent,
                    IndentEntityContent     = current.IndentEntityContent,
                    IndentFieldContent      = current.IndentFieldContent,
                    IndentNamespace         = current.IndentNamespace,
                    IndentMultiLines        = current.IndentMultiLines,
                    IndentPreprocessorLines = current.IndentPreprocessorLines,
                    KeywordCase             = current.KeywordCase,
                    TrimTrailingWhitespace  = current.TrimTrailingWhitespace,
                    InsertFinalNewline      = current.InsertFinalNewline,
                };

                _logger.LogInformation(
                    "WorkspaceScanner: applying project defaults from {Proj} — " +
                    "Dialect={D} StandardDefs={S} IncludePaths={I}",
                    Path.GetFileName(proj),
                    merged.Dialect, merged.StandardDefs, merged.IncludePaths);

                _configService.Apply(merged);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WorkspaceScanner: failed to read project settings");
            }
        }

        // ====================================================================
        // .xsproj exclusion helpers (step 19)
        // ====================================================================

        /// <summary>
        /// Finds all <c>*.xsproj</c> files under <paramref name="rootPath"/> and
        /// collects every <c>&lt;Compile Remove="…"/&gt;</c> glob pattern into a
        /// list of compiled <see cref="Regex"/> objects for fast matching.
        /// </summary>
        private List<Regex> LoadExcludePatterns(string rootPath)
        {
            var patterns = new List<Regex>();
            try
            {
                foreach (var proj in Directory.EnumerateFiles(
                    rootPath, "*.xsproj", SearchOption.AllDirectories))
                {
                    var doc = XDocument.Load(proj);
                    var projDir = Path.GetDirectoryName(proj) ?? rootPath;

                    foreach (var el in doc.Descendants()
                        .Where(e => e.Name.LocalName == "Compile"
                                 && e.Attribute("Remove") != null))
                    {
                        string raw = el.Attribute("Remove")!.Value;
                        try
                        {
                            patterns.Add(GlobToRegex(raw, projDir));
                            _logger.LogDebug(
                                "WorkspaceScanner: exclude pattern '{Pattern}' from {Proj}",
                                raw, Path.GetFileName(proj));
                        }
                        catch { /* skip malformed globs */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WorkspaceScanner: failed to load .xsproj exclusions");
            }
            return patterns;
        }

        /// <summary>
        /// Converts an MSBuild glob pattern relative to <paramref name="baseDir"/>
        /// into a <see cref="Regex"/>.  Handles the common forms:
        /// <c>*.ext</c>, <c>**/*.ext</c>, <c>folder/**</c>, and exact file names.
        /// </summary>
        private static Regex GlobToRegex(string glob, string baseDir)
        {
            // Make the glob absolute so we can match against full paths.
            string absBase = baseDir.Replace('\\', '/').TrimEnd('/');

            // Convert glob syntax to regex.
            string pattern = Regex.Escape(glob.Replace('\\', '/'))
                .Replace(@"\*\*/", "(.+/)?")   // **/ → any directory depth
                .Replace(@"\*\*",  ".+")        // ** → any
                .Replace(@"\*",    "[^/]*")     // *  → any within one segment
                .Replace(@"\?",    "[^/]");     // ?  → any single char

            // Anchor to the project directory.
            string full = Regex.Escape(absBase) + "/" + pattern;
            return new Regex(full, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private static bool IsExcluded(string filePath, string rootPath, List<Regex> patterns)
        {
            if (patterns.Count == 0) return false;
            string normalised = filePath.Replace('\\', '/');
            foreach (var rx in patterns)
                if (rx.IsMatch(normalised)) return true;
            return false;
        }

        // Matches paths that pass through a \bin\ or \obj\ directory segment,
        // which are SDK build-output folders and should never be indexed.
        private static bool IsOutputDirectory(string path)
        {
            // Use the OS separator so the check works on both Windows and Linux.
            var sep = Path.DirectorySeparatorChar;
            return path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
                || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class NullErrorListener : VsParser.IErrorListener
        {
            public void ReportError(string fileName, LinePositionSpan span,
                string errorCode, string message, object[] args) { }

            public void ReportWarning(string fileName, LinePositionSpan span,
                string errorCode, string message, object[] args) { }
        }
    }
}
