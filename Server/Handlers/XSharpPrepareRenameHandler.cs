using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles <c>textDocument/prepareRename</c>.
    /// <para>
    /// Called by VS Code before showing the rename input box.  This handler:
    /// <list type="number">
    ///   <item>Extracts the identifier under the cursor.</item>
    ///   <item>Rejects the rename if the word is empty or is a reserved XSharp
    ///         keyword (checked against the same keyword map used by
    ///         <see cref="XSharpFormattingHandler"/>).</item>
    ///   <item>Returns the word's range so VS Code can pre-select it in the
    ///         rename input box.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class XSharpPrepareRenameHandler : PrepareRenameHandlerBase
    {
        private readonly XSharpDocumentService _documentService;
        private readonly ILogger<XSharpPrepareRenameHandler> _logger;

        public XSharpPrepareRenameHandler(
            XSharpDocumentService documentService,
            ILogger<XSharpPrepareRenameHandler> logger)
        {
            _documentService = documentService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override RenameRegistrationOptions CreateRegistrationOptions(
            RenameCapability capability,
            ClientCapabilities clientCapabilities)
            => new RenameRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                PrepareProvider  = true,
            };

        /// <inheritdoc/>
        public override Task<RangeOrPlaceholderRange?> Handle(
            PrepareRenameParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                var uri = request.TextDocument.Uri;
                var pos = request.Position;

                if (!_documentService.TryGetText(uri, out var text))
                    return Task.FromResult<RangeOrPlaceholderRange?>(null);

                var (word, range) = ExtractWord(text, pos);

                if (string.IsNullOrEmpty(word) || range == null)
                    return Task.FromResult<RangeOrPlaceholderRange?>(null);

                // Reject keywords — they cannot be renamed.
                if (XSharpFormattingHandler.KeywordMap.Values.Contains(word.ToUpperInvariant(),
                        StringComparer.Ordinal))
                {
                    _logger.LogDebug("PrepareRename: '{Word}' is a keyword — rejected", word);
                    return Task.FromResult<RangeOrPlaceholderRange?>(null);
                }

                _logger.LogDebug("PrepareRename: '{Word}' accepted", word);
                return Task.FromResult<RangeOrPlaceholderRange?>(
                    new RangeOrPlaceholderRange(new PlaceholderRange
                    {
                        Range       = range,
                        Placeholder = word,
                    }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PrepareRename failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<RangeOrPlaceholderRange?>(null);
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static (string Word, LspRange? Range) ExtractWord(string text, Position pos)
        {
            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return (string.Empty, null);

            string line = lines[(int)pos.Line].TrimEnd('\r');
            int col     = Math.Min((int)pos.Character, line.Length);

            int start = col;
            while (start > 0 && IsIdentChar(line[start - 1])) start--;

            int end = col;
            while (end < line.Length && IsIdentChar(line[end])) end++;

            if (end == start) return (string.Empty, null);

            string word  = line.Substring(start, end - start);
            var range = new LspRange(
                new Position(pos.Line, start),
                new Position(pos.Line, end));

            return (word, range);
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
