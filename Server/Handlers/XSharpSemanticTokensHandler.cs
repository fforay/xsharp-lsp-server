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

using XSharpLanguageServer.Services;
namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles LSP semantic token requests (<c>textDocument/semanticTokens/full</c>
    /// and <c>textDocument/semanticTokens/range</c>) for XSharp documents.
    /// <para>
    /// Semantic tokens provide richer syntax highlighting than TextMate grammars:
    /// each token carries a <em>type</em> (e.g. <c>keyword</c>, <c>type</c>,
    /// <c>comment</c>) and optional <em>modifiers</em> (e.g. <c>static</c>,
    /// <c>readonly</c>), which the editor maps to theme colours.
    /// </para>
    /// <para>
    /// This handler reads the pre-parsed token stream from
    /// <see cref="XSharpDocumentService"/> (populated by <see cref="VsParser.Parse"/>)
    /// and classifies each token using the static helper methods on
    /// <see cref="XSharpLexer"/> (e.g. <see cref="XSharpLexer.IsKeyword"/>,
    /// <see cref="XSharpLexer.IsType"/>, etc.).
    /// </para>
    /// </summary>
    public class XSharpSemanticTokensHandler : SemanticTokensHandlerBase
    {
        private readonly ILogger<XSharpSemanticTokensHandler> _logger;
        private readonly XSharpDocumentService _documentService;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpSemanticTokensHandler(
            XSharpDocumentService documentService,
            ILogger<XSharpSemanticTokensHandler> logger)
        {
            _documentService = documentService;
            _logger = logger;
        }

        /// <summary>
        /// Declares the semantic token legend (the ordered list of token type and
        /// modifier strings) negotiated with the client during initialisation.
        /// We mirror the client's own capability lists so the indices always align.
        /// </summary>
        protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
            SemanticTokensCapability capability,
            ClientCapabilities clientCapabilities)
        {
            // The legend must match exactly what we pass to SemanticTokensBuilder.Push().
            // Using the client's own lists guarantees the indices are consistent.
            var legend = new SemanticTokensLegend
            {
                TokenTypes = capability.TokenTypes,
                TokenModifiers = capability.TokenModifiers
            };

            return new SemanticTokensRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                Legend = legend,
                Full = true,    // support full-document token request
                Range = true    // support range-based token request (visible viewport)
            };
        }

        /// <summary>
        /// Creates the <see cref="SemanticTokensDocument"/> that accumulates tokens
        /// before they are encoded and sent to the client.
        /// </summary>
        protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
            ITextDocumentIdentifierParams @params,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
        }

        /// <summary>
        /// Iterates over the token stream from the parse cache and pushes classified
        /// tokens into <paramref name="builder"/>.
        /// <para>
        /// Token classification priority (first match wins):
        /// <c>comment</c> → <c>macro</c> → <c>type</c> → <c>modifier</c> →
        /// <c>keyword</c> → <c>string</c> → <c>number</c> → <c>variable</c> →
        /// <c>operator</c>.
        /// </para>
        /// </summary>
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
                    _logger.LogWarning("No parse result available for {Uri}", identifier.TextDocument.Uri);
                    return;
                }

                var stream = parsed.TokenStream as BufferedTokenStream;
                if (stream == null) return;

                // GetTokens() returns every token on every channel, including
                // whitespace (hidden) and comments (hidden / XML doc channel).
                var tokens = stream.GetTokens();

                _logger.LogInformation(
                    "Tokenising {Uri}: {Count} tokens",
                    identifier.TextDocument.Uri, tokens.Count);

                foreach (XSharpToken token in tokens)
                {
                    // Respect client-side cancellation (e.g. user closes the file).
                    cancellationToken.ThrowIfCancellationRequested();

                    string? tokenType = ClassifyToken(token);
                    if (tokenType == null) continue;

                    // XSharp token lines are 1-based; LSP positions are 0-based.
                    int line = token.Line - 1;
                    int col  = token.Column;
                    int len  = token.Text?.Length ?? 0;

                    // Skip synthetic or malformed tokens.
                    if (line < 0 || len <= 0) continue;

                    builder.Push(line, col, len, tokenType, Array.Empty<string>());
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation path — not an error, no logging needed.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tokenization failed for {Uri}", identifier.TextDocument.Uri);
            }
        }

        /// <summary>
        /// Maps a single <see cref="XSharpToken"/> to an LSP semantic token type string,
        /// or returns <c>null</c> if the token should not be highlighted
        /// (e.g. whitespace, end-of-statement markers).
        /// <para>
        /// The checks are ordered so that more specific categories take precedence
        /// over broader ones. For example, <c>IsComment</c> is tested before
        /// <c>IsKeyword</c> so that XML doc comment tokens (<c>DOC_COMMENT</c>)
        /// are coloured as comments rather than keywords.
        /// </para>
        /// </summary>
        /// <param name="token">The token to classify.</param>
        /// <returns>An LSP <see cref="SemanticTokenType"/> constant, or <c>null</c>.</returns>
        private static string? ClassifyToken(XSharpToken token)
        {
            int type = token.Type;

            // Single-line (//, &&), multi-line (/* */), and XML doc (///) comments.
            // Checked first so DOC_COMMENT is not accidentally matched as a keyword.
            if (XSharpLexer.IsComment(type))
                return SemanticTokenType.Comment;

            // Preprocessor directives: #define, #include, #ifdef, #region, …
            if (XSharpLexer.IsPPKeyword(type))
                return SemanticTokenType.Macro;

            // Built-in value types and reference types:
            // STRING, INT, DWORD, OBJECT, ARRAY, LOGIC, USUAL, VOID, …
            if (XSharpLexer.IsType(type))
                return SemanticTokenType.Type;

            // Access modifiers and other declaration modifiers:
            // PUBLIC, PRIVATE, PROTECTED, INTERNAL, STATIC, VIRTUAL, ABSTRACT, …
            if (XSharpLexer.IsModifier(type))
                return SemanticTokenType.Modifier;

            // All remaining language keywords:
            // FUNCTION, PROCEDURE, CLASS, IF, WHILE, RETURN, …
            if (XSharpLexer.IsKeyword(type))
                return SemanticTokenType.Keyword;

            // String literals in all XSharp flavours:
            // "plain", e"escaped", i"interpolated", [bracketed], c"char", 0h (binary), …
            if (XSharpLexer.IsString(type))
                return SemanticTokenType.String;

            // Numeric, boolean, date/time, symbol, and null literal constants.
            if (XSharpLexer.IsLiteral(type))
                return SemanticTokenType.Number;

            // Plain identifiers (variable names, function names before resolution).
            if (XSharpLexer.IsIdentifier(type))
                return SemanticTokenType.Variable;

            // Arithmetic, comparison, logical, bitwise, and member-access operators.
            if (XSharpLexer.IsOperator(type))
                return SemanticTokenType.Operator;

            // Whitespace, EOS, LINE_CONT, and other structural tokens — not coloured.
            return null;
        }
    }
}
