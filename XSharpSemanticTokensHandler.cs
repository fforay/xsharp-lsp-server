using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XSharpLanguageServer
{
    public class XSharpSemanticTokensHandler : SemanticTokensHandlerBase
    {
        private readonly ILogger<XSharpSemanticTokensHandler> _logger;
        private readonly XSharpDocumentService _documentService;

        public XSharpSemanticTokensHandler(
            XSharpDocumentService documentService,
            ILogger<XSharpSemanticTokensHandler> logger)
        {
            _documentService = documentService;
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
                if (!_documentService.TryGetParsed(identifier.TextDocument.Uri, out var parsed)
                    || parsed.TokenStream == null)
                {
                    _logger.LogWarning("No parse result for {Uri}", identifier.TextDocument.Uri);
                    return;
                }

                var stream = parsed.TokenStream as BufferedTokenStream;
                if (stream == null) return;

                // GetTokens(true) includes tokens on all channels (hidden, comments, etc.)
                var tokens = stream.GetTokens();

                _logger.LogInformation("Tokenising {Uri}: {Count} tokens", identifier.TextDocument.Uri, tokens.Count);

                foreach (XSharpToken token in tokens)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string? tokenType = ClassifyToken(token);
                    if (tokenType == null) continue;

                    // LSP lines are 0-based; XSharp token lines are 1-based
                    int line = token.Line - 1;
                    int col  = token.Column;
                    int len  = token.Text?.Length ?? 0;

                    if (line < 0 || len <= 0) continue;

                    builder.Push(line, col, len, tokenType, Array.Empty<string>());
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation — do not log as error
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tokenization failed for {Uri}", identifier.TextDocument.Uri);
            }
        }

        /// <summary>
        /// Maps an XSharp token type to an LSP SemanticTokenType string.
        /// Returns null for tokens that should not be highlighted.
        /// </summary>
        private static string? ClassifyToken(XSharpToken token)
        {
            int type = token.Type;

            // Comments (check before keyword so DOC_COMMENT is handled here)
            if (XSharpLexer.IsComment(type))
                return SemanticTokenType.Comment;

            // Preprocessor directives (#define, #include, #ifdef, …)
            if (XSharpLexer.IsPPKeyword(type))
                return SemanticTokenType.Macro;

            // Built-in type keywords (STRING, INT, DWORD, OBJECT, …)
            if (XSharpLexer.IsType(type))
                return SemanticTokenType.Type;

            // Modifier keywords (PUBLIC, PRIVATE, PROTECTED, STATIC, VIRTUAL, …)
            if (XSharpLexer.IsModifier(type))
                return SemanticTokenType.Modifier;

            // All other keywords
            if (XSharpLexer.IsKeyword(type))
                return SemanticTokenType.Keyword;

            // String literals (all variants: bracketed, interpolated, escaped, …)
            if (XSharpLexer.IsString(type))
                return SemanticTokenType.String;

            // Numeric, boolean, date, and other literal constants
            if (XSharpLexer.IsLiteral(type))
                return SemanticTokenType.Number;

            // Identifiers
            if (XSharpLexer.IsIdentifier(type))
                return SemanticTokenType.Variable;

            // Operators
            if (XSharpLexer.IsOperator(type))
                return SemanticTokenType.Operator;

            return null;
        }
    }
}
