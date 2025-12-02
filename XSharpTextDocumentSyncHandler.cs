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
        private readonly IDictionary<DocumentUri, string> _documents;
        private readonly ILanguageServerFacade _server; // injecté par le framework

        public XSharpTextDocumentSyncHandler(
            IDictionary<DocumentUri, string> documents,
            ILanguageServerFacade server)
        {
            _documents = documents;
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
            _documents[request.TextDocument.Uri] = request.TextDocument.Text ?? string.Empty;
            return Unit.Task;
        }


        public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
        {
            if (!_documents.ContainsKey(request.TextDocument.Uri))
                _documents[request.TextDocument.Uri] = string.Empty;

            var oldText = _documents[request.TextDocument.Uri];

            foreach (var change in request.ContentChanges)
            {
                oldText = ApplyChange(oldText, change);
            }

            _documents[request.TextDocument.Uri] = oldText;

            _server?.SendNotification("workspace/semanticTokens/refresh");

            return Unit.Task;
        }

        public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
        {
            _documents.Remove(request.TextDocument.Uri);
            return Unit.Task;
        }

        public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
        {
            if (request.Text != null)
            {
                _documents[request.TextDocument.Uri] = request.Text;
            }

            _server.SendNotification("workspace/semanticTokens/refresh");
            return Unit.Task;
        }


        private string ApplyChange(string oldText, TextDocumentContentChangeEvent change)
        {
            // Si Range est null, VSCode envoie le document complet
            if (change.Range == null)
            {
                return change.Text;
            }

            var lines = oldText.Split('\n').ToList();

            var startLine = change.Range.Start.Line;
            var startChar = change.Range.Start.Character;
            var endLine = change.Range.End.Line;
            var endChar = change.Range.End.Character;

            // Partie avant la modification
            var before = lines[startLine].Substring(0, startChar);

            // Partie après la modification
            var after = lines[endLine].Substring(endChar);

            // Supprimer les lignes affectées
            lines.RemoveRange(startLine, endLine - startLine + 1);

            // Insérer la nouvelle ligne (avant + texte inséré + après)
            var newTextLines = change.Text.Split('\n');

            if (newTextLines.Length == 1)
            {
                lines.Insert(startLine, before + newTextLines[0] + after);
            }
            else
            {
                // Première ligne = before + première ligne du texte inséré
                lines.Insert(startLine, before + newTextLines[0]);

                // Lignes intermédiaires
                for (int i = 1; i < newTextLines.Length - 1; i++)
                {
                    lines.Insert(startLine + i, newTextLines[i]);
                }

                // Dernière ligne = dernière ligne du texte inséré + after
                lines.Insert(startLine + newTextLines.Length - 1, newTextLines.Last() + after);
            }

            return string.Join("\n", lines);
        }
    }
}