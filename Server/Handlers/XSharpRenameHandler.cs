using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Models;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>textDocument/rename</c> LSP request ("Rename symbol").
    /// <para>
    /// Strategy — scope-aware in two tiers:
    /// </para>
    /// <para>
    /// <b>Tier A — Local / parameter rename (scope-limited):</b>
    /// If the identifier under the cursor is determined to be a <c>LOCAL</c>
    /// variable or a parameter of the enclosing function/method (via
    /// <see cref="XSharpScopeHelper.IsLocalOrParameter"/>), only token
    /// occurrences within that function's line range in the <em>current file</em>
    /// are replaced.  Other files and other functions with identically-named
    /// locals are left untouched.
    /// </para>
    /// <para>
    /// <b>Tier B — Global / type / member rename (project-wide):</b>
    /// For any other symbol (function, class, method, global, define, …), all
    /// occurrences across the entire project are replaced — both open documents
    /// (live text) and closed files (workspace index, last-saved state).
    /// Open-document results take precedence over indexed results so that
    /// unsaved edits are not lost.
    /// </para>
    /// </summary>
    public class XSharpRenameHandler : RenameHandlerBase
    {
        private readonly XSharpDocumentService       _documentService;
        private readonly XSharpWorkspaceIndex        _workspaceIndex;
        private readonly ILogger<XSharpRenameHandler> _logger;

        public XSharpRenameHandler(
            XSharpDocumentService           documentService,
            XSharpWorkspaceIndex            workspaceIndex,
            ILogger<XSharpRenameHandler>    logger)
        {
            _documentService = documentService;
            _workspaceIndex  = workspaceIndex;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override RenameRegistrationOptions CreateRegistrationOptions(
            RenameCapability    capability,
            ClientCapabilities  clientCapabilities)
            => new RenameRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                PrepareProvider  = false,
            };

        /// <inheritdoc/>
        public override Task<WorkspaceEdit?> Handle(
            RenameParams      request,
            CancellationToken cancellationToken)
        {
            try
            {
                string newName = request.NewName?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(newName))
                    return Task.FromResult<WorkspaceEdit?>(null);

                var uri = request.TextDocument.Uri;

                if (!_documentService.TryGetText(uri, out var text))
                    return Task.FromResult<WorkspaceEdit?>(null);

                string oldName = XSharpReferencesHandler.ExtractWord(text, request.Position);
                if (string.IsNullOrEmpty(oldName))
                    return Task.FromResult<WorkspaceEdit?>(null);

                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<WorkspaceEdit?>(null);

                _logger.LogInformation(
                    "Rename: '{OldName}' → '{NewName}'", oldName, newName);

                // ── Scope check ──────────────────────────────────────────────
                bool isScoped = false;
                int  scopeStart = 0, scopeEnd = int.MaxValue;

                if (_documentService.TryGetParsed(uri, out var parsed)
                    && parsed.Tree != null)
                {
                    isScoped = XSharpScopeHelper.IsLocalOrParameter(
                        parsed.Tree, request.Position, oldName,
                        out scopeStart, out scopeEnd);
                }

                WorkspaceEdit result = isScoped
                    ? BuildScopedEdit(uri, oldName, newName, scopeStart, scopeEnd)
                    : BuildProjectWideEdit(oldName, newName);

                _logger.LogInformation(
                    "Rename ({Scope}): {EditCount} edit(s)",
                    isScoped ? $"scope [{scopeStart}–{scopeEnd}]" : "project-wide",
                    CountEdits(result));

                return Task.FromResult<WorkspaceEdit?>(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rename failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<WorkspaceEdit?>(null);
            }
        }

        // ====================================================================
        // Tier A — scope-limited (local / parameter)
        // ====================================================================

        private WorkspaceEdit BuildScopedEdit(
            DocumentUri uri,
            string      oldName,
            string      newName,
            int         scopeStart,
            int         scopeEnd)
        {
            string? filePath = uri.GetFileSystemPath();
            var edits = new List<TextEdit>();

            // Dedup by line — open-document results win over indexed.
            var seenLines = new HashSet<int>();

            // Open-document live text first (may have unsaved edits).
            foreach (var (tokUri, line, col, len) in _documentService.FindTokenLocations(oldName))
            {
                if (tokUri != uri) continue;
                if (line < scopeStart || line > scopeEnd) continue;
                seenLines.Add(line);
                edits.Add(MakeEdit(line, col, len, newName));
            }

            // Workspace index for any lines not covered by the live scan.
            if (filePath != null)
            {
                foreach (var tok in _workspaceIndex.FindTokenLocations(oldName))
                {
                    if (!string.Equals(tok.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (tok.Line < scopeStart || tok.Line > scopeEnd) continue;
                    if (seenLines.Contains(tok.Line)) continue;   // open-doc wins
                    edits.Add(MakeEdit(tok.Line, tok.Col, tok.Text.Length, newName));
                }
            }

            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
            if (edits.Count > 0)
                changes[uri] = edits;

            return new WorkspaceEdit { Changes = changes };
        }

        // ====================================================================
        // Tier B — project-wide (global / type / member)
        // ====================================================================

        private WorkspaceEdit BuildProjectWideEdit(string oldName, string newName)
        {
            // Keyed by (normalised file path, line) — open-file results win.
            var byKey = new Dictionary<(string FilePath, int Line),
                                       (DocumentUri Uri, int Col, int Len)>(
                FileLineComparer.Instance);

            // Workspace index (all indexed files, last-saved state).
            foreach (var tok in _workspaceIndex.FindTokenLocations(oldName))
                byKey[(tok.FilePath, tok.Line)] =
                    (DocumentUri.FromFileSystemPath(tok.FilePath), tok.Col, tok.Text.Length);

            // Open documents (unsaved edits override indexed entries).
            foreach (var (uri, line, col, len) in _documentService.FindTokenLocations(oldName))
            {
                string? fp = uri.GetFileSystemPath();
                byKey[(fp ?? uri.ToString(), line)] = (uri, col, len);
            }

            var byUri = new Dictionary<string, List<TextEdit>>();
            foreach (var ((_, line), (uri, col, len)) in byKey)
            {
                string key = uri.ToString();
                if (!byUri.TryGetValue(key, out var list))
                    byUri[key] = list = new List<TextEdit>();
                list.Add(MakeEdit(line, col, len, newName));
            }

            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
            foreach (var (key, edits) in byUri)
                changes[DocumentUri.Parse(key)] = edits;

            return new WorkspaceEdit { Changes = changes };
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static TextEdit MakeEdit(int line, int col, int len, string newText)
            => new TextEdit
            {
                Range   = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                              new Position(line, col),
                              new Position(line, col + len)),
                NewText = newText,
            };

        private static int CountEdits(WorkspaceEdit edit)
        {
            int total = 0;
            if (edit.Changes != null)
                foreach (var v in edit.Changes.Values)
                    foreach (var _ in v) total++;
            return total;
        }

        private sealed class FileLineComparer
            : IEqualityComparer<(string FilePath, int Line)>
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
