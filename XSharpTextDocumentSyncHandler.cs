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

namespace XSharpLanguageServer
{
    public class XSharpTextDocumentSyncHandler : TextDocumentSyncHandlerBase
    {
        private readonly XSharpDocumentService _documentService;
        private readonly ILanguageServerFacade _server;

        public XSharpTextDocumentSyncHandler(
            XSharpDocumentService documentService,
            ILanguageServerFacade server)
        {
            _documentService = documentService;
            _server = server;
        }

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

        public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
            => new TextDocumentAttributes(uri, "xsharp");

        public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
        {
            _documentService.UpdateText(
                request.TextDocument.Uri,
                request.TextDocument.Text ?? string.Empty);

            return Unit.Task;
        }

        public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
        {
            if (!_documentService.TryGetText(request.TextDocument.Uri, out var oldText))
                oldText = string.Empty;

            foreach (var change in request.ContentChanges)
            {
                oldText = ApplyChange(oldText, change);
            }

            _documentService.UpdateText(request.TextDocument.Uri, oldText);

            _server?.SendNotification("workspace/semanticTokens/refresh");

            return Unit.Task;
        }

        public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
        {
            _documentService.Remove(request.TextDocument.Uri);
            return Unit.Task;
        }

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
        /// Applies a single LSP incremental change to the document text.
        /// Handles both \n and \r\n line endings correctly.
        /// </summary>
        private static string ApplyChange(string oldText, TextDocumentContentChangeEvent change)
        {
            // Full document replacement
            if (change.Range == null)
                return change.Text;

            // Normalise to \n for internal processing, remembering the original ending
            bool hasCr = oldText.Contains('\r');
            string normalised = hasCr ? oldText.Replace("\r\n", "\n").Replace("\r", "\n") : oldText;

            var lines = normalised.Split('\n').ToList();

            int startLine = change.Range.Start.Line;
            int startChar = change.Range.Start.Character;
            int endLine   = change.Range.End.Line;
            int endChar   = change.Range.End.Character;

            // Guard against out-of-range positions (can happen with rapid edits)
            startLine = System.Math.Min(startLine, lines.Count - 1);
            endLine   = System.Math.Min(endLine,   lines.Count - 1);
            startChar = System.Math.Min(startChar, lines[startLine].Length);
            endChar   = System.Math.Min(endChar,   lines[endLine].Length);

            string before = lines[startLine][..startChar];
            string after  = lines[endLine][endChar..];

            lines.RemoveRange(startLine, endLine - startLine + 1);

            // Normalise the inserted text the same way
            string insertNormalised = change.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            var newLines = insertNormalised.Split('\n');

            if (newLines.Length == 1)
            {
                lines.Insert(startLine, before + newLines[0] + after);
            }
            else
            {
                lines.Insert(startLine, before + newLines[0]);
                for (int i = 1; i < newLines.Length - 1; i++)
                    lines.Insert(startLine + i, newLines[i]);
                lines.Insert(startLine + newLines.Length - 1, newLines[^1] + after);
            }

            // Restore original line endings
            string result = string.Join("\n", lines);
            return hasCr ? result.Replace("\n", "\r\n") : result;
        }
    }
}
