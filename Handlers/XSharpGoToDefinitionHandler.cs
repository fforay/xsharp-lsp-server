using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace XSharpLanguageServer
{
    /// <summary>
    /// Handles the <c>textDocument/definition</c> LSP request.
    /// <para>
    /// When the cursor rests on an identifier, this handler:
    /// <list type="number">
    ///   <item>Extracts the word under the cursor.</item>
    ///   <item>Looks it up in the XSharp IntelliSense database via
    ///         <see cref="XSharpDatabaseService.FindExact"/>.</item>
    ///   <item>Returns a <see cref="LocationOrLocationLinks"/> pointing to
    ///         the file/line recorded in the database.</item>
    /// </list>
    /// Returns <c>null</c> when the database is unavailable or no match is found.
    /// </para>
    /// </summary>
    public class XSharpGoToDefinitionHandler : DefinitionHandlerBase
    {
        private readonly XSharpDocumentService          _documentService;
        private readonly XSharpDatabaseService          _dbService;
        private readonly ILogger<XSharpGoToDefinitionHandler> _logger;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpGoToDefinitionHandler(
            XSharpDocumentService                 documentService,
            XSharpDatabaseService                 dbService,
            ILogger<XSharpGoToDefinitionHandler>  logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override DefinitionRegistrationOptions CreateRegistrationOptions(
            DefinitionCapability  capability,
            ClientCapabilities    clientCapabilities)
            => new DefinitionRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        /// <inheritdoc/>
        public override Task<LocationOrLocationLinks?> Handle(
            DefinitionParams   request,
            CancellationToken  cancellationToken)
        {
            try
            {
                if (!_dbService.IsAvailable)
                    return Task.FromResult<LocationOrLocationLinks?>(null);

                var uri  = request.TextDocument.Uri;
                var pos  = request.Position;

                var text = _documentService.TryGetText(uri, out var txt) ? txt : null;
                if (text == null)
                    return Task.FromResult<LocationOrLocationLinks?>(null);

                string word = ExtractWord(text, pos);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<LocationOrLocationLinks?>(null);

                string? filePath = uri.GetFileSystemPath();
                var symbol = _dbService.FindExact(word, filePath);

                if (symbol == null || string.IsNullOrEmpty(symbol.FileName))
                    return Task.FromResult<LocationOrLocationLinks?>(null);

                var location = new Location
                {
                    Uri   = DocumentUri.FromFileSystemPath(symbol.FileName),
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(symbol.StartLine, symbol.StartCol),
                        new Position(symbol.StartLine, symbol.StartCol + symbol.Name.Length)),
                };

                return Task.FromResult<LocationOrLocationLinks?>(
                    new LocationOrLocationLinks(location));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GoToDefinition failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<LocationOrLocationLinks?>(null);
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
