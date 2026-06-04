using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;
using XSharpLanguageServer.Models;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>textDocument/references</c> LSP request ("Find all references").
    /// <para>
    /// Strategy:
    /// <list type="number">
    ///   <item>Extract the identifier under the cursor.</item>
    ///   <item>Scan the token stream of every currently open document for
    ///         matching <c>ID</c> tokens via
    ///         <see cref="XSharpDocumentService.FindTokenLocations"/>.</item>
    ///   <item>If <see cref="ReferenceContext.IncludeDeclaration"/> is <c>true</c>,
    ///         also add declaration sites from the XSharp IntelliSense database
    ///         (<c>X#Model.xsdb</c>) via <see cref="XSharpDatabaseService.FindAllByName"/>.</item>
    /// </list>
    /// Limitation: usages in files not currently open are not returned.
    /// </para>
    /// </summary>
    public class XSharpReferencesHandler : ReferencesHandlerBase
    {
        private readonly XSharpDocumentService             _documentService;
        private readonly XSharpDatabaseService             _dbService;
        private readonly XSharpWorkspaceIndex              _workspaceIndex;
        private readonly ILogger<XSharpReferencesHandler> _logger;

        public XSharpReferencesHandler(
            XSharpDocumentService              documentService,
            XSharpDatabaseService              dbService,
            XSharpWorkspaceIndex               workspaceIndex,
            ILogger<XSharpReferencesHandler>   logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
            _workspaceIndex  = workspaceIndex;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override ReferenceRegistrationOptions CreateRegistrationOptions(
            ReferenceCapability     capability,
            ClientCapabilities      clientCapabilities)
            => new ReferenceRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        /// <inheritdoc/>
        public override Task<LocationContainer?> Handle(
            ReferenceParams   request,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!_documentService.TryGetText(request.TextDocument.Uri, out var text))
                    return Task.FromResult<LocationContainer?>(null);

                string word = ExtractWord(text, request.Position);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<LocationContainer?>(null);

                _logger.LogInformation("References: searching for '{Word}'", word);

                var locations = new List<Location>();

                // ── Closed files: workspace index token map ───────────────────
                // Covers all project files indexed at startup or on save.
                // Results are keyed by (normalised path, line) so open-file
                // results can override them below.
                var indexedByKey = new Dictionary<(string FilePath, int Line), Location>(
                    FileLineComparer.Instance);

                foreach (var tok in _workspaceIndex.FindTokenLocations(word))
                {
                    int len = tok.Text.Length;
                    var loc = new Location
                    {
                        Uri   = DocumentUri.FromFileSystemPath(tok.FilePath),
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(tok.Line, tok.Col),
                            new Position(tok.Line, tok.Col + len)),
                    };
                    indexedByKey[(tok.FilePath, tok.Line)] = loc;
                }

                // ── Open files: live token scan (may contain unsaved edits) ──
                // Replace any indexed entry for the same file+line so unsaved
                // changes are reflected accurately.
                var openFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (uri, line, col, len) in _documentService.FindTokenLocations(word))
                {
                    string? fp = uri.GetFileSystemPath();
                    if (fp != null) openFilePaths.Add(fp);

                    indexedByKey[(fp ?? uri.ToString(), line)] = new Location
                    {
                        Uri   = uri,
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(line, col),
                            new Position(line, col + len)),
                    };
                }

                locations.AddRange(indexedByKey.Values);

                // Workspace index declaration sites when client requests them.
                // (Assembly symbols have no meaningful source locations — index only.)
                if (request.Context?.IncludeDeclaration == true)
                {
                    foreach (var decl in _workspaceIndex.FindAllByName(word))
                    {
                        if (string.IsNullOrEmpty(decl.FileName)) continue;
                        var declUri = DocumentUri.FromFileSystemPath(decl.FileName);
                        // Skip if already covered by token scan.
                        if (locations.Exists(l =>
                                l.Uri == declUri && l.Range.Start.Line == decl.StartLine))
                            continue;

                        locations.Add(new Location
                        {
                            Uri   = declUri,
                            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                new Position(decl.StartLine, decl.StartCol),
                                new Position(decl.StartLine, decl.StartCol + word.Length)),
                        });
                    }
                }

                _logger.LogInformation(
                    "References: {Count} location(s) for '{Word}'", locations.Count, word);

                return Task.FromResult<LocationContainer?>(
                    locations.Count > 0 ? new LocationContainer(locations) : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "References failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<LocationContainer?>(null);
            }
        }

        internal static string ExtractWord(string text, Position pos)
        {
            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return string.Empty;
            string line = lines[pos.Line];
            int    col  = Math.Min((int)pos.Character, line.Length);
            int start = col;
            while (start > 0 && IsIdentChar(line[start - 1])) start--;
            int end = col;
            while (end < line.Length && IsIdentChar(line[end])) end++;
            return line.Substring(start, end - start);
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private sealed class FileLineComparer : IEqualityComparer<(string FilePath, int Line)>
        {
            public static readonly FileLineComparer Instance = new();

            public bool Equals((string FilePath, int Line) x, (string FilePath, int Line) y)
                => x.Line == y.Line
                && string.Equals(x.FilePath, y.FilePath, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string FilePath, int Line) obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FilePath),
                    obj.Line);
        }
    }
}
