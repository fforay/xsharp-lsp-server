using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles <c>textDocument/documentHighlight</c> — highlights every occurrence
    /// of the identifier under the cursor within the current file.
    /// <para>
    /// Strategy: extract the word under the cursor, then collect all matching
    /// identifier token locations in the current document from the live token scan
    /// (<see cref="XSharpDocumentService.FindTokenLocations"/>), falling back to the
    /// workspace index for the same file when the document is not open.
    /// All occurrences are returned with <see cref="DocumentHighlightKind.Text"/>.
    /// </para>
    /// </summary>
    public class XSharpDocumentHighlightHandler : DocumentHighlightHandlerBase
    {
        private readonly XSharpDocumentService                      _documentService;
        private readonly XSharpWorkspaceIndex                       _workspaceIndex;
        private readonly ILogger<XSharpDocumentHighlightHandler>    _logger;

        public XSharpDocumentHighlightHandler(
            XSharpDocumentService                   documentService,
            XSharpWorkspaceIndex                    workspaceIndex,
            ILogger<XSharpDocumentHighlightHandler> logger)
        {
            _documentService = documentService;
            _workspaceIndex  = workspaceIndex;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
            DocumentHighlightCapability capability,
            ClientCapabilities          clientCapabilities)
            => new DocumentHighlightRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        /// <inheritdoc/>
        public override Task<DocumentHighlightContainer?> Handle(
            DocumentHighlightParams request,
            CancellationToken       cancellationToken)
        {
            try
            {
                if (!_documentService.TryGetText(request.TextDocument.Uri, out var text))
                    return Task.FromResult<DocumentHighlightContainer?>(null);

                string word = XSharpReferencesHandler.ExtractWord(text, request.Position);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<DocumentHighlightContainer?>(null);

                _logger.LogInformation("DocumentHighlight: '{Word}' in {Uri}", word, request.TextDocument.Uri);

                var highlights = new Dictionary<int, DocumentHighlight>();

                // ── Live scan of the open document (reflects unsaved edits) ───
                bool foundInOpen = false;
                foreach (var (uri, line, col, len) in _documentService.FindTokenLocations(word))
                {
                    if (uri != request.TextDocument.Uri) continue;
                    foundInOpen = true;
                    highlights[line] = new DocumentHighlight
                    {
                        Kind  = DocumentHighlightKind.Text,
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(line, col),
                            new Position(line, col + len)),
                    };
                }

                // ── Workspace index fallback (file not currently open in editor) ──
                if (!foundInOpen)
                {
                    string? filePath = request.TextDocument.Uri.GetFileSystemPath();
                    if (filePath != null)
                    {
                        foreach (var tok in _workspaceIndex.FindTokenLocations(word))
                        {
                            if (!string.Equals(tok.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                                continue;
                            highlights.TryAdd(tok.Line, new DocumentHighlight
                            {
                                Kind  = DocumentHighlightKind.Text,
                                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                    new Position(tok.Line, tok.Col),
                                    new Position(tok.Line, tok.Col + tok.Text.Length)),
                            });
                        }
                    }
                }

                _logger.LogInformation(
                    "DocumentHighlight: {Count} occurrence(s) of '{Word}'", highlights.Count, word);

                return Task.FromResult<DocumentHighlightContainer?>(
                    highlights.Count > 0
                        ? new DocumentHighlightContainer(highlights.Values)
                        : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DocumentHighlight failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<DocumentHighlightContainer?>(null);
            }
        }
    }
}
