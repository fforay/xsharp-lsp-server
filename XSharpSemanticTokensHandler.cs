using LanguageService.CodeAnalysis.Text;
using LanguageService.CodeAnalysis.XSharp;
using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XSharp.Parser;

namespace XSharpLanguageServer
{
    public class XSharpSemanticTokensHandler : SemanticTokensHandlerBase, VsParser.IErrorListener
    {
        private readonly ILogger<XSharpSemanticTokensHandler> _logger;
        private readonly IDictionary<DocumentUri, string> _documents;

        public XSharpSemanticTokensHandler(
            IDictionary<DocumentUri, string> documents,
            ILogger<XSharpSemanticTokensHandler> logger = null)
        {
            _documents = documents;
            _logger = logger;
        }

        protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
            SemanticTokensCapability capability,
            ClientCapabilities clientCapabilities)
        {
            var legend = new SemanticTokensLegend
            {
                TokenTypes = capability.TokenTypes,
                TokenModifiers = capability.TokenModifiers
            };

            return new SemanticTokensRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                Legend = legend,
                Full = true,
                Range = true
            };
        }

        protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
            ITextDocumentIdentifierParams @params,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
        }

        protected override async Task Tokenize(
            SemanticTokensBuilder builder,
            ITextDocumentIdentifierParams identifier,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!_documents.TryGetValue(identifier.TextDocument.Uri, out var code))
                {
                    _logger.LogWarning("Cannot find Document {Uri} in buffer", identifier.TextDocument.Uri);
                    return;
                }

                _logger.LogInformation("Tokenising document {Uri} (Length {Length})", identifier.TextDocument.Uri, code.Length);

                string fileName = identifier.TextDocument.Uri.GetFileSystemPath();
                var parseOptions = XSharpParseOptions.Default;

                bool ok = VsParser.Lex(code, fileName, parseOptions, this, out var tokenStream, out var _);
                var stream = tokenStream as BufferedTokenStream;
                var tokens = stream.GetTokens();

                _logger.LogInformation("After Tokenising : {Count} tokens.", tokens.Count);


                foreach (XSharpToken token in tokens)
                {
                    string tokenType = string.Empty;

                    if (XSharpLexer.IsKeyword(token.Type)) tokenType = SemanticTokenType.Keyword;
                    else if (XSharpLexer.IsIdentifier(token.Type)) tokenType = SemanticTokenType.Variable;
                    else if (XSharpLexer.IsComment(token.Type)) tokenType = SemanticTokenType.Comment;
                    else if (XSharpLexer.IsModifier(token.Type)) tokenType = SemanticTokenType.Modifier;
                    else if (XSharpLexer.IsString(token.Type)) tokenType = SemanticTokenType.String;

                    if (!string.IsNullOrEmpty(tokenType))
                    {
                        builder.Push(token.Line - 1, token.Column, token.Text.Length, tokenType, Array.Empty<string>());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erreur lors de la tokenization");
            }
        }

        public void ReportError(string fileName, LinePositionSpan span, string errorCode, string message, object[] args) { }
        public void ReportWarning(string fileName, LinePositionSpan span, string errorCode, string message, object[] args) { }
    }
}