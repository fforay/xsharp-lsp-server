using LanguageService.SyntaxTree;
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

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>textDocument/references</c> LSP request ("Find all references").
    /// <para>
    /// Strategy (partial — open documents only for usages, full project for declarations):
    /// <list type="number">
    ///   <item>Extract the identifier under the cursor.</item>
    ///   <item>Scan the token stream of every currently open document for tokens
    ///         whose text matches the identifier (case-insensitive).  Each match
    ///         becomes a <see cref="Location"/>.</item>
    ///   <item>If <see cref="ReferenceContext.IncludeDeclaration"/> is <c>true</c>,
    ///         also add the declaration sites from the XSharp IntelliSense database
    ///         (<c>X#Model.xsdb</c>) via <see cref="XSharpDatabaseService.FindAllByName"/>.</item>
    /// </list>
    /// Limitations: usages in files that are not currently open are not returned.
    /// </para>
    /// </summary>
    public class XSharpReferencesHandler : ReferencesHandlerBase
    {
        // XSharp lexer token type for a plain identifier (non-keyword).
        // Determined by reflection: XSharpLexer.ID = 351.
        private const int TokenTypeId = 351;

        private readonly XSharpDocumentService             _documentService;
        private readonly XSharpDatabaseService             _dbService;
        private readonly ILogger<XSharpReferencesHandler> _logger;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpReferencesHandler(
            XSharpDocumentService              documentService,
            XSharpDatabaseService              dbService,
            ILogger<XSharpReferencesHandler>   logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
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
                var uri = request.TextDocument.Uri;
                var pos = request.Position;

                if (!_documentService.TryGetText(uri, out var text))
                    return Task.FromResult<LocationContainer?>(null);

                string word = ExtractWord(text, pos);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<LocationContainer?>(null);

                _logger.LogInformation("References: searching for '{Word}'", word);

                var locations = new List<Location>();

                // ----------------------------------------------------------------
                // 1. Scan token streams of all open documents
                // ----------------------------------------------------------------
                var openUris = _documentService.GetOpenUris();
                foreach (var openUri in openUris)
                {
                    if (!_documentService.TryGetParsed(openUri, out var parsed))
                        continue;
                    if (parsed.TokenStream == null)
                        continue;

                    var docText = parsed.Text;
                    var lines = docText.Split('\n');

                    // Walk all tokens on all channels.
                    var stream = parsed.TokenStream as BufferedTokenStream;
                    if (stream == null) continue;

                    // Fetch all tokens (including hidden channel).
                    var tokens = stream.GetTokens();
                    if (tokens == null) continue;

                    foreach (var token in tokens)
                    {
                        // Only plain identifier tokens (type 351).
                        if (token.Type != TokenTypeId) continue;
                        if (!string.Equals(token.Text, word, StringComparison.OrdinalIgnoreCase)) continue;

                        // XSharp lines are 1-based; LSP is 0-based.
                        int line = Math.Max(0, token.Line - 1);
                        int col  = token.Column;

                        locations.Add(new Location
                        {
                            Uri   = openUri,
                            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                new Position(line, col),
                                new Position(line, col + token.Text.Length)),
                        });
                    }
                }

                // ----------------------------------------------------------------
                // 2. Add DB declaration sites (if client wants declarations too)
                // ----------------------------------------------------------------
                if (request.Context?.IncludeDeclaration == true && _dbService.IsAvailable)
                {
                    var declarations = _dbService.FindAllByName(word);
                    foreach (var decl in declarations)
                    {
                        if (string.IsNullOrEmpty(decl.FileName)) continue;

                        var declUri = DocumentUri.FromFileSystemPath(decl.FileName);

                        // Skip if we already have a token-scan result for the same
                        // file/line (avoids duplicating entries for open documents).
                        bool alreadyCovered = locations.Exists(l =>
                            l.Uri == declUri && l.Range.Start.Line == decl.StartLine);

                        if (!alreadyCovered)
                        {
                            locations.Add(new Location
                            {
                                Uri   = declUri,
                                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                    new Position(decl.StartLine, decl.StartCol),
                                    new Position(decl.StartLine, decl.StartCol + word.Length)),
                            });
                        }
                    }
                }

                _logger.LogInformation(
                    "References: found {Count} location(s) for '{Word}'", locations.Count, word);

                return Task.FromResult<LocationContainer?>(
                    locations.Count > 0 ? new LocationContainer(locations) : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "References failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<LocationContainer?>(null);
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static string ExtractWord(string text, Position pos)
        {
            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return string.Empty;

            string line = lines[pos.Line];
            int    col  = Math.Min((int)pos.Character, line.Length);

            int start = col;
            while (start > 0 && IsIdentChar(line[start - 1]))
                start--;

            int end = col;
            while (end < line.Length && IsIdentChar(line[end]))
                end++;

            return line.Substring(start, end - start);
        }

        private static bool IsIdentChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';
    }
}
