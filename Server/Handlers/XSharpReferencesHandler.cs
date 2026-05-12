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
    /// Strategy:
    /// <list type="number">
    ///   <item>Extract the identifier under the cursor.</item>
    ///   <item>Scan the token stream of every currently open document for
    ///         matching <c>ID</c> tokens via
    ///         <see cref="XSharpDocumentService.FindTokenLocations"/>.</item>
    ///   <item>If <see cref="ReferenceContext.IncludeDeclaration"/> is <c>true</c>,
    ///         also add declaration sites from the XSharp IntelliSense database
    ///         (<c>X#Model.xsdb</c>) via <see cref="XSharpDatabaseService.FindAllByName"/>.</item>
    /// </list>
    /// Limitation: usages in files not currently open are not returned.
    /// </para>
    /// </summary>
    public class XSharpReferencesHandler : ReferencesHandlerBase
    {
        private readonly XSharpDocumentService             _documentService;
        private readonly XSharpDatabaseService             _dbService;
        private readonly ILogger<XSharpReferencesHandler> _logger;

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
                if (!_documentService.TryGetText(request.TextDocument.Uri, out var text))
                    return Task.FromResult<LocationContainer?>(null);

                string word = ExtractWord(text, request.Position);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<LocationContainer?>(null);

                _logger.LogInformation("References: searching for '{Word}'", word);

                var locations = new List<Location>();

                // Token-scan all open documents.
                foreach (var (uri, line, col, len) in _documentService.FindTokenLocations(word))
                {
                    locations.Add(new Location
                    {
                        Uri   = uri,
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(line, col),
                            new Position(line, col + len)),
                    });
                }

                // DB declaration sites when client requests them.
                if (request.Context?.IncludeDeclaration == true && _dbService.IsAvailable)
                {
                    foreach (var decl in _dbService.FindAllByName(word))
                    {
                        if (string.IsNullOrEmpty(decl.FileName)) continue;
                        var declUri = DocumentUri.FromFileSystemPath(decl.FileName);
                        // Skip if already covered by token scan.
                        if (locations.Exists(l =>
                                l.Uri == declUri && l.Range.Start.Line == decl.StartLine))
                            continue;

                        locations.Add(new Location
                        {
                            Uri   = declUri,
                            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                new Position(decl.StartLine, decl.StartCol),
                                new Position(decl.StartLine, decl.StartCol + word.Length)),
                        });
                    }
                }

                _logger.LogInformation(
                    "References: {Count} location(s) for '{Word}'", locations.Count, word);

                return Task.FromResult<LocationContainer?>(
                    locations.Count > 0 ? new LocationContainer(locations) : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "References failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<LocationContainer?>(null);
            }
        }

        internal static string ExtractWord(string text, Position pos)
        {
            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return string.Empty;
            string line = lines[pos.Line];
            int    col  = Math.Min((int)pos.Character, line.Length);
            int start = col;
            while (start > 0 && IsIdentChar(line[start - 1])) start--;
            int end = col;
            while (end < line.Length && IsIdentChar(line[end])) end++;
            return line.Substring(start, end - start);
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
