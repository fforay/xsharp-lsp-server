using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using XSharpLanguageServer.Services;
namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles LSP document synchronisation notifications for XSharp files:
    /// <c>textDocument/didOpen</c>, <c>textDocument/didChange</c>,
    /// <c>textDocument/didSave</c>, and <c>textDocument/didClose</c>.
    /// <para>
    /// On every open, change, or save the handler forwards the updated text to
    /// <see cref="XSharpDocumentService"/>, which re-parses the document and
    /// publishes fresh diagnostics. A <c>workspace/semanticTokens/refresh</c>
    /// notification is also sent so the client re-requests token colours.
    /// </para>
    /// </summary>
    public class XSharpTextDocumentSyncHandler : TextDocumentSyncHandlerBase
    {
        private readonly XSharpDocumentService _documentService;

        /// <summary>
        /// The server facade used to send notifications back to the client
        /// (e.g. <c>workspace/semanticTokens/refresh</c>).
        /// </summary>
        private readonly ILanguageServerFacade _server;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpTextDocumentSyncHandler(
            XSharpDocumentService documentService,
            ILanguageServerFacade server)
        {
            _documentService = documentService;
            _server = server;
        }

        /// <summary>
        /// Declares which documents this handler manages and how changes are
        /// delivered. We request <see cref="TextDocumentSyncKind.Incremental"/>
        /// so the client sends only the changed ranges on each keystroke, and
        /// <c>IncludeText = true</c> on save so we always get the authoritative
        /// text at save time.
        /// </summary>
        protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
            TextSynchronizationCapability capability,
            ClientCapabilities clientCapabilities)
        {
            return new TextDocumentSyncRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                Change = TextDocumentSyncKind.Incremental,
                Save = new SaveOptions { IncludeText = true }
            };
        }

        /// <summary>
        /// Tells the OmniSharp framework that URIs served by this handler carry
        /// the <c>"xsharp"</c> language identifier.
        /// </summary>
        public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
            => new TextDocumentAttributes(uri, "xsharp");

        /// <summary>
        /// Called when the client opens a document. The full text is included in
        /// the notification, so we store it immediately and trigger a first parse.
        /// </summary>
        public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
        {
            _documentService.UpdateText(
                request.TextDocument.Uri,
                request.TextDocument.Text ?? string.Empty);

            return Unit.Task;
        }

        /// <summary>
        /// Called when the client reports one or more incremental edits.
        /// Each <see cref="TextDocumentContentChangeEvent"/> describes a range
        /// replacement. Changes are applied sequentially to the buffered text,
        /// then the updated text is forwarded to <see cref="XSharpDocumentService"/>
        /// for re-parsing.
        /// </summary>
        public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
        {
            if (!_documentService.TryGetText(request.TextDocument.Uri, out var oldText))
                oldText = string.Empty;

            foreach (var change in request.ContentChanges)
            {
                oldText = ApplyChange(oldText, change);
            }

            _documentService.UpdateText(request.TextDocument.Uri, oldText);

            // Ask the client to re-request semantic tokens for this document.
            _server?.SendNotification("workspace/semanticTokens/refresh");

            return Unit.Task;
        }

        /// <summary>
        /// Called when the client closes a document. We discard all cached state
        /// (text buffer and parse result) to avoid holding onto stale data.
        /// </summary>
        public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
        {
            _documentService.Remove(request.TextDocument.Uri);
            return Unit.Task;
        }

        /// <summary>
        /// Called when the client saves a document. Because we registered
        /// <c>IncludeText = true</c> in <see cref="CreateRegistrationOptions"/>,
        /// the saved text is always present and we use it as the authoritative version,
        /// discarding any pending incremental edits.
        /// </summary>
        public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
        {
            if (request.Text != null)
            {
                _documentService.UpdateText(request.TextDocument.Uri, request.Text);
            }

            _server.SendNotification("workspace/semanticTokens/refresh");
            return Unit.Task;
        }

        /// <summary>
        /// Applies a single LSP incremental change event to <paramref name="oldText"/>
        /// and returns the resulting string.
        /// <para>
        /// The method normalises both the document text and the inserted text to <c>\n</c>
        /// line endings before processing, then restores the original line ending style
        /// (<c>\r\n</c> or <c>\n</c>) in the result. This avoids the off-by-one errors
        /// that arise when splitting on <c>\n</c> while the text still contains <c>\r</c>.
        /// </para>
        /// </summary>
        /// <param name="oldText">The current document text.</param>
        /// <param name="change">The change event from the client.</param>
        /// <returns>The document text after the change has been applied.</returns>
        private static string ApplyChange(string oldText, TextDocumentContentChangeEvent change)
        {
            // A null Range means the client is sending a full document replacement
            // (can happen when switching from Full to Incremental sync mid-session).
            if (change.Range == null)
                return change.Text;

            // Detect and normalise line endings.
            bool hasCr = oldText.Contains('\r');
            string normalised = hasCr ? oldText.Replace("\r\n", "\n").Replace("\r", "\n") : oldText;

            var lines = normalised.Split('\n').ToList();

            int startLine = change.Range.Start.Line;
            int startChar = change.Range.Start.Character;
            int endLine   = change.Range.End.Line;
            int endChar   = change.Range.End.Character;

            // Clamp positions to avoid index-out-of-range during rapid edits where
            // the client may send a position that is momentarily ahead of our buffer.
            startLine = System.Math.Min(startLine, lines.Count - 1);
            endLine   = System.Math.Min(endLine,   lines.Count - 1);
            startChar = System.Math.Min(startChar, lines[startLine].Length);
            endChar   = System.Math.Min(endChar,   lines[endLine].Length);

            // Preserve the text before the start and after the end of the changed range.
            string before = lines[startLine][..startChar];
            string after  = lines[endLine][endChar..];

            // Remove all lines that fall within the changed range.
            lines.RemoveRange(startLine, endLine - startLine + 1);

            // Normalise the inserted text to \n as well.
            string insertNormalised = change.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            var newLines = insertNormalised.Split('\n');

            if (newLines.Length == 1)
            {
                // Single-line replacement: merge before + inserted + after on one line.
                lines.Insert(startLine, before + newLines[0] + after);
            }
            else
            {
                // Multi-line insertion:
                //   first line  = before  + first inserted line
                //   middle lines = inserted lines verbatim
                //   last line   = last inserted line + after
                lines.Insert(startLine, before + newLines[0]);
                for (int i = 1; i < newLines.Length - 1; i++)
                    lines.Insert(startLine + i, newLines[i]);
                lines.Insert(startLine + newLines.Length - 1, newLines[^1] + after);
            }

            // Restore the original line ending style.
            string result = string.Join("\n", lines);
            return hasCr ? result.Replace("\n", "\r\n") : result;
        }
    }
}
