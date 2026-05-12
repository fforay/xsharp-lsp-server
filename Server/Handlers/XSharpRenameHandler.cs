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
    /// Handles the <c>textDocument/rename</c> LSP request ("Rename symbol").
    /// <para>
    /// Strategy:
    /// <list type="number">
    ///   <item>Extract the identifier under the cursor.</item>
    ///   <item>Scan the token stream of every currently open document via
    ///         <see cref="XSharpDocumentService.FindTokenLocations"/> — the same
    ///         helper used by Find References.</item>
    ///   <item>Group matches by file URI and build one <see cref="TextEdit"/> array
    ///         per file, replacing every matched token span with the new name.</item>
    ///   <item>Return a <see cref="WorkspaceEdit"/> containing all edits.</item>
    /// </list>
    /// Limitation: only open documents are scanned; closed files are not modified.
    /// </para>
    /// </summary>
    public class XSharpRenameHandler : RenameHandlerBase
    {
        private readonly XSharpDocumentService           _documentService;
        private readonly ILogger<XSharpRenameHandler>    _logger;

        public XSharpRenameHandler(
            XSharpDocumentService          documentService,
            ILogger<XSharpRenameHandler>   logger)
        {
            _documentService = documentService;
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

                if (!_documentService.TryGetText(request.TextDocument.Uri, out var text))
                    return Task.FromResult<WorkspaceEdit?>(null);

                string oldName = XSharpReferencesHandler.ExtractWord(text, request.Position);
                if (string.IsNullOrEmpty(oldName))
                    return Task.FromResult<WorkspaceEdit?>(null);

                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<WorkspaceEdit?>(null);

                _logger.LogInformation(
                    "Rename: '{OldName}' → '{NewName}'", oldName, newName);

                // Collect all token matches across open documents.
                var matches = _documentService.FindTokenLocations(oldName);
                if (matches.Count == 0)
                    return Task.FromResult<WorkspaceEdit?>(null);

                // Group by URI → TextEdit list.
                var byUri = new Dictionary<string, List<TextEdit>>();
                foreach (var (uri, line, col, len) in matches)
                {
                    string key = uri.ToString();
                    if (!byUri.TryGetValue(key, out var list))
                        byUri[key] = list = new List<TextEdit>();

                    list.Add(new TextEdit
                    {
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(line, col),
                            new Position(line, col + len)),
                        NewText = newName,
                    });
                }

                // Build WorkspaceEdit.
                var changes = new Dictionary<
                    OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri,
                    IEnumerable<TextEdit>>();

                foreach (var (key, edits) in byUri)
                {
                    var docUri =
                        OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.Parse(key);
                    changes[docUri] = edits;
                }

                _logger.LogInformation(
                    "Rename: {EditCount} edit(s) across {FileCount} file(s)",
                    matches.Count, byUri.Count);

                return Task.FromResult<WorkspaceEdit?>(
                    new WorkspaceEdit { Changes = changes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rename failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<WorkspaceEdit?>(null);
            }
        }
    }
}
