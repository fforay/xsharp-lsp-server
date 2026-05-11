using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XSharpLanguageServer
{
    /// <summary>
    /// Handles the <c>textDocument/hover</c> LSP request.
    /// <para>
    /// When the cursor rests on an identifier, this handler:
    /// <list type="number">
    ///   <item>Extracts the word under the cursor from the current document text.</item>
    ///   <item>Looks it up in the XSharp IntelliSense database (<c>X#Model.xsdb</c>)
    ///         via <see cref="XSharpDatabaseService.FindExact"/>.</item>
    ///   <item>Returns a Markdown hover card containing the prototype
    ///         (<c>Sourcecode</c>) and optional XML doc comment.</item>
    /// </list>
    /// Returns <c>null</c> (no hover) when the database is unavailable or the
    /// word is not found.
    /// </para>
    /// </summary>
    public class XSharpHoverHandler : HoverHandlerBase
    {
        private readonly XSharpDocumentService    _documentService;
        private readonly XSharpDatabaseService    _dbService;
        private readonly ILogger<XSharpHoverHandler> _logger;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpHoverHandler(
            XSharpDocumentService       documentService,
            XSharpDatabaseService       dbService,
            ILogger<XSharpHoverHandler> logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override HoverRegistrationOptions CreateRegistrationOptions(
            HoverCapability capability,
            ClientCapabilities clientCapabilities)
            => new HoverRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        /// <inheritdoc/>
        public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
        {
            try
            {
                if (!_dbService.IsAvailable)
                    return Task.FromResult<Hover?>(null);

                var uri  = request.TextDocument.Uri;
                var pos  = request.Position;

                var text = _documentService.TryGetText(uri, out var txt) ? txt : null;
                if (text == null)
                    return Task.FromResult<Hover?>(null);

                string word = ExtractWord(text, pos);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<Hover?>(null);

                string? filePath = uri.GetFileSystemPath();
                var symbol = _dbService.FindExact(word, filePath);
                if (symbol == null)
                    return Task.FromResult<Hover?>(null);

                var md = BuildMarkdown(symbol);
                var hover = new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind  = MarkupKind.Markdown,
                        Value = md,
                    }),
                };

                return Task.FromResult<Hover?>(hover);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hover failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<Hover?>(null);
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        /// <summary>
        /// Extracts the word (XSharp identifier) under <paramref name="pos"/>
        /// from <paramref name="text"/>.
        /// </summary>
        private static string ExtractWord(string text, Position pos)
        {
            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return string.Empty;

            string line   = lines[pos.Line];
            int    col    = Math.Min((int)pos.Character, line.Length);

            // Scan backward to find start of identifier
            int start = col;
            while (start > 0 && IsIdentChar(line[start - 1]))
                start--;

            // Scan forward to find end of identifier
            int end = col;
            while (end < line.Length && IsIdentChar(line[end]))
                end++;

            return line.Substring(start, end - start);
        }

        private static bool IsIdentChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Builds a Markdown hover string from a <see cref="DbSymbol"/>.</summary>
        private static string BuildMarkdown(DbSymbol symbol)
        {
            var sb = new StringBuilder();

            // Code block with the prototype
            if (!string.IsNullOrWhiteSpace(symbol.Sourcecode))
            {
                sb.AppendLine("```xsharp");
                sb.AppendLine(symbol.Sourcecode.Trim());
                sb.AppendLine("```");
            }
            else
            {
                // Fallback: just the name
                sb.AppendLine("```xsharp");
                sb.AppendLine(symbol.Name);
                sb.AppendLine("```");
            }

            // XML doc comment (may contain raw XML — strip tags for readability)
            if (!string.IsNullOrWhiteSpace(symbol.XmlComments))
            {
                sb.AppendLine();
                sb.AppendLine(StripXmlTags(symbol.XmlComments.Trim()));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Strips XML tags from a doc-comment string, leaving plain text.
        /// Handles <c>&lt;summary&gt;</c>, <c>&lt;param&gt;</c>, etc.
        /// </summary>
        private static string StripXmlTags(string xml)
        {
            // Simple regex-free approach: remove angle-bracket content
            var sb   = new StringBuilder(xml.Length);
            bool tag = false;
            foreach (char c in xml)
            {
                if      (c == '<') tag = true;
                else if (c == '>') tag = false;
                else if (!tag)     sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
