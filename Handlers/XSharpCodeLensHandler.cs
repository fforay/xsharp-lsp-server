using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
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
    /// Handles <c>textDocument/codeLens</c> and <c>codeLens/resolve</c>.
    /// <para>
    /// Phase 1 (<c>textDocument/codeLens</c>): walks the parse tree of the
    /// requested document and emits one <see cref="CodeLens"/> per named
    /// top-level declaration (function, procedure, method, class, interface,
    /// structure, enum, property, event).  The symbol name is stored in
    /// <see cref="CodeLens.Data"/> so the resolve phase can retrieve it.
    /// </para>
    /// <para>
    /// Phase 2 (<c>codeLens/resolve</c>): counts the number of
    /// <c>ID</c>-token occurrences matching the symbol name across all
    /// currently open documents via
    /// <see cref="XSharpDocumentService.FindTokenLocations"/>, then sets the
    /// lens title to <c>"N references"</c>.  The command is intentionally
    /// left non-executable (no command ID) so clicking it is a no-op; it
    /// serves purely as an informational annotation.
    /// </para>
    /// </summary>
    public class XSharpCodeLensHandler : CodeLensHandlerBase
    {
        private readonly XSharpDocumentService          _documentService;
        private readonly ILogger<XSharpCodeLensHandler> _logger;

        public XSharpCodeLensHandler(
            XSharpDocumentService           documentService,
            ILogger<XSharpCodeLensHandler>  logger)
        {
            _documentService = documentService;
            _logger          = logger;
        }

        // ----------------------------------------------------------------
        // Registration
        // ----------------------------------------------------------------
        protected override CodeLensRegistrationOptions CreateRegistrationOptions(
            CodeLensCapability  capability,
            ClientCapabilities  clientCapabilities)
            => new CodeLensRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                ResolveProvider  = true,
            };

        // ----------------------------------------------------------------
        // Phase 1 — emit one CodeLens per declaration
        // ----------------------------------------------------------------
        public override Task<CodeLensContainer?> Handle(
            CodeLensParams    request,
            CancellationToken cancellationToken)
        {
            try
            {
                var uri = request.TextDocument.Uri;

                if (!_documentService.TryGetParsed(uri, out var parsed) || parsed.Tree == null)
                    return Task.FromResult<CodeLensContainer?>(null);

                var lenses = new List<CodeLens>();
                CollectLenses(parsed.Tree, lenses, cancellationToken);

                _logger.LogInformation(
                    "CodeLens: {Count} lens(es) for {Uri}", lenses.Count, uri);

                return Task.FromResult<CodeLensContainer?>(
                    lenses.Count > 0 ? new CodeLensContainer(lenses) : null);
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult<CodeLensContainer?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CodeLens failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<CodeLensContainer?>(null);
            }
        }

        // ----------------------------------------------------------------
        // Phase 2 — resolve: count references and set the title
        // ----------------------------------------------------------------
        public override Task<CodeLens> Handle(
            CodeLens          request,
            CancellationToken cancellationToken)
        {
            try
            {
                string? name = request.Data?.Type == JTokenType.String
                    ? request.Data.Value<string>()
                    : null;

                if (string.IsNullOrEmpty(name))
                    return Task.FromResult(WithTitle(request, "0 references"));

                // Count token occurrences across all open documents.
                // Subtract 1 to exclude the declaration itself.
                int total = _documentService.FindTokenLocations(name).Count;
                int refs  = Math.Max(0, total - 1);

                string title = refs == 1 ? "1 reference" : $"{refs} references";
                return Task.FromResult(WithTitle(request, title));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CodeLens resolve failed");
                return Task.FromResult(WithTitle(request, "? references"));
            }
        }

        // ----------------------------------------------------------------
        // Parse tree walker
        // ----------------------------------------------------------------

        /// <summary>
        /// Recursively walks <paramref name="node"/> and appends one
        /// <see cref="CodeLens"/> for every recognised named declaration.
        /// </summary>
        private static void CollectLenses(
            IParseTree        node,
            List<CodeLens>    lenses,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (node is XSharpParserRuleContext ctx)
            {
                var (name, startLine) = ExtractDeclaration(ctx);
                if (name != null && startLine >= 0)
                {
                    lenses.Add(new CodeLens
                    {
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(startLine, 0),
                            new Position(startLine, 0)),
                        Data  = JToken.FromObject(name),
                    });
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
                CollectLenses(node.GetChild(i), lenses, ct);
        }

        /// <summary>
        /// Extracts the declared name and 0-based start line from a recognised
        /// parse tree node.  Returns <c>(null, -1)</c> for non-declaration nodes.
        /// </summary>
        private static (string? Name, int Line) ExtractDeclaration(
            XSharpParserRuleContext ctx)
        {
            // XSharp token lines are 1-based; LSP is 0-based.
            int L(LanguageService.SyntaxTree.IToken? t) =>
                t == null ? -1 : Math.Max(0, t.Line - 1);

            return ctx switch
            {
                XSharpParser.FuncprocContext fp   =>
                    (fp.Sig?.Id?.GetText(),      L(fp.Start)),
                XSharpParser.MethodContext m       =>
                    (m.Sig?.Id?.GetText(),        L(m.Start)),
                XSharpParser.Class_Context cls     =>
                    (cls.Id?.GetText(),           L(cls.Start)),
                XSharpParser.Interface_Context ifc =>
                    (ifc.Id?.GetText(),           L(ifc.Start)),
                XSharpParser.Structure_Context s   =>
                    (s.Id?.GetText(),             L(s.Start)),
                XSharpParser.Enum_Context en       =>
                    (en.Id?.GetText(),            L(en.Start)),
                XSharpParser.PropertyContext prop  =>
                    (prop.Id?.GetText(),          L(prop.Start)),
                XSharpParser.Event_Context evt     =>
                    (evt.Id?.GetText(),           L(evt.Start)),
                XSharpParser.ConstructorContext co =>
                    ("CONSTRUCTOR",              L(co.Start)),
                _                                 =>
                    (null, -1),
            };
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static CodeLens WithTitle(CodeLens lens, string title)
            => new CodeLens
            {
                Range   = lens.Range,
                Data    = lens.Data,
                Command = new Command { Title = title },
            };
    }
}
