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
    /// Handles the <c>textDocument/signatureHelp</c> LSP request.
    /// <para>
    /// When the cursor is inside a function call's argument list, this handler:
    /// <list type="number">
    ///   <item>Scans leftward from the cursor to find the enclosing function name
    ///         and count which comma-separated argument is active.</item>
    ///   <item>Queries the XSharp IntelliSense database for all overloads of that
    ///         function via <see cref="XSharpDatabaseService.FindOverloads"/>.</item>
    ///   <item>Returns a <see cref="SignatureHelp"/> response with one
    ///         <see cref="SignatureInformation"/> per overload and the active
    ///         parameter index highlighted.</item>
    /// </list>
    /// Returns <c>null</c> when the database is unavailable or no function name
    /// can be identified.
    /// </para>
    /// </summary>
    public class XSharpSignatureHelpHandler : SignatureHelpHandlerBase
    {
        private readonly XSharpDocumentService              _documentService;
        private readonly XSharpDatabaseService              _dbService;
        private readonly ILogger<XSharpSignatureHelpHandler> _logger;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpSignatureHelpHandler(
            XSharpDocumentService                  documentService,
            XSharpDatabaseService                  dbService,
            ILogger<XSharpSignatureHelpHandler>    logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
            SignatureHelpCapability capability,
            ClientCapabilities      clientCapabilities)
            => new SignatureHelpRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                // Trigger on '(' and ','
                TriggerCharacters = new Container<string>("(", ","),
            };

        /// <inheritdoc/>
        public override Task<SignatureHelp?> Handle(
            SignatureHelpParams request,
            CancellationToken   cancellationToken)
        {
            try
            {
                if (!_dbService.IsAvailable)
                    return Task.FromResult<SignatureHelp?>(null);

                var uri  = request.TextDocument.Uri;
                var pos  = request.Position;

                var text = _documentService.TryGetText(uri, out var txt) ? txt : null;
                if (text == null)
                    return Task.FromResult<SignatureHelp?>(null);

                if (!TryExtractCallContext(text, pos, out string funcName, out int activeParam))
                    return Task.FromResult<SignatureHelp?>(null);

                var overloads = _dbService.FindOverloads(funcName);
                if (overloads.Count == 0)
                    return Task.FromResult<SignatureHelp?>(null);

                var signatures = new List<SignatureInformation>();
                foreach (var sym in overloads)
                {
                    string label = sym.Sourcecode?.Trim() ?? sym.Name;
                    signatures.Add(new SignatureInformation
                    {
                        Label         = label,
                        Documentation = sym.XmlComments != null
                            ? new StringOrMarkupContent(new MarkupContent
                              {
                                  Kind  = MarkupKind.Markdown,
                                  Value = StripXmlTags(sym.XmlComments),
                              })
                            : null,
                    });
                }

                return Task.FromResult<SignatureHelp?>(new SignatureHelp
                {
                    Signatures      = new Container<SignatureInformation>(signatures),
                    ActiveSignature = 0,
                    ActiveParameter = activeParam,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SignatureHelp failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<SignatureHelp?>(null);
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        /// <summary>
        /// Scans leftward from <paramref name="pos"/> in <paramref name="text"/>
        /// to find the enclosing function name and the 0-based active parameter index.
        /// Returns <c>false</c> if no open parenthesis is found.
        /// </summary>
        private static bool TryExtractCallContext(
            string   text,
            Position pos,
            out string funcName,
            out int   activeParam)
        {
            funcName    = string.Empty;
            activeParam = 0;

            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return false;

            string line = lines[pos.Line];
            int col     = Math.Min((int)pos.Character, line.Length);

            // Count commas and find the matching '('
            int depth   = 0;
            int commas  = 0;

            for (int i = col - 1; i >= 0; i--)
            {
                char c = line[i];
                if      (c == ')') depth++;
                else if (c == '(' && depth > 0) depth--;
                else if (c == '(' && depth == 0)
                {
                    // Found the opening paren — extract the function name to the left
                    int nameEnd = i;
                    while (nameEnd > 0 && char.IsWhiteSpace(line[nameEnd - 1]))
                        nameEnd--;

                    int nameStart = nameEnd;
                    while (nameStart > 0 && IsIdentChar(line[nameStart - 1]))
                        nameStart--;

                    funcName    = line.Substring(nameStart, nameEnd - nameStart);
                    activeParam = commas;
                    return !string.IsNullOrEmpty(funcName);
                }
                else if (c == ',' && depth == 0)
                {
                    commas++;
                }
            }

            return false;
        }

        private static bool IsIdentChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';

        private static string StripXmlTags(string xml)
        {
            var  sb  = new System.Text.StringBuilder(xml.Length);
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
