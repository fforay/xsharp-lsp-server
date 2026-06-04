using LanguageService.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

            var files = SourceExtensions
                .SelectMany(ext => Directory.EnumerateFiles(
                    rootPath, ext, SearchOption.AllDirectories))
                .Where(f => !IsOutputDirectory(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation("WorkspaceScanner: found {Count} source file(s) to index", files.Count);

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
