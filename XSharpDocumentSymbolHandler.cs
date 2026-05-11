using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using LanguageService.SyntaxTree.Tree;
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
    /// <summary>
    /// Handles the <c>textDocument/documentSymbol</c> LSP request.
    /// <para>
    /// Returns a hierarchical tree of <see cref="DocumentSymbol"/> objects that
    /// represent every declared entity in the file: namespaces, types (class,
    /// interface, structure, enum, delegate, VO struct/union), global functions and
    /// procedures, methods, constructors, destructors, properties, events, and fields.
    /// </para>
    /// <para>
    /// The symbol tree is used by editors for:
    /// <list type="bullet">
    ///   <item>The outline / breadcrumb panel</item>
    ///   <item><c>Ctrl+Shift+O</c> — Go to Symbol in File</item>
    ///   <item>Code folding hints (complementing the dedicated folding-range handler)</item>
    /// </list>
    /// </para>
    /// </summary>
    public class XSharpDocumentSymbolHandler : DocumentSymbolHandlerBase
    {
        private readonly XSharpDocumentService _documentService;
        private readonly ILogger<XSharpDocumentSymbolHandler> _logger;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpDocumentSymbolHandler(
            XSharpDocumentService documentService,
            ILogger<XSharpDocumentSymbolHandler> logger)
        {
            _documentService = documentService;
            _logger = logger;
        }

        /// <summary>
        /// Registers this handler for the <c>"xsharp"</c> language and declares
        /// support for the hierarchical <see cref="DocumentSymbol"/> format
        /// (as opposed to the legacy flat <c>SymbolInformation</c> format).
        /// </summary>
        protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
            DocumentSymbolCapability capability,
            ClientCapabilities clientCapabilities)
        {
            return new DocumentSymbolRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };
        }

        /// <summary>
        /// Entry point for <c>textDocument/documentSymbol</c> requests.
        /// Retrieves the cached parse tree and walks it to produce the symbol list.
        /// </summary>
#nullable disable warnings
        public override Task<SymbolInformationOrDocumentSymbolContainer> Handle(
            DocumentSymbolParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!_documentService.TryGetParsed(request.TextDocument.Uri, out var parsed)
                    || parsed.Tree == null)
                {
                    _logger.LogWarning("No parse tree available for {Uri}", request.TextDocument.Uri);
                    return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer());
                }

                var symbols = new List<SymbolInformationOrDocumentSymbol>();
                var walker  = new SymbolWalker(cancellationToken);

                // Walk the top-level children of the source/compilation unit.
                foreach (var child in walker.GetTopLevelSymbols(parsed.Tree))
                {
                    symbols.Add(new SymbolInformationOrDocumentSymbol(child));
                }

                _logger.LogInformation(
                    "DocumentSymbol: {Count} top-level symbol(s) for {Uri}",
                    symbols.Count, request.TextDocument.Uri);

                return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer(symbols));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DocumentSymbol failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer());
            }
        }
#nullable restore warnings

        // ====================================================================
        // Inner walker — converts the ANTLR parse tree into DocumentSymbol objects
        // ====================================================================

        /// <summary>
        /// Walks an <see cref="XSharpParserRuleContext"/> tree and produces
        /// <see cref="DocumentSymbol"/> objects for every declared entity.
        /// <para>
        /// The walker is stateless and re-entrant; a new instance is created per request.
        /// </para>
        /// </summary>
        private sealed class SymbolWalker
        {
            private readonly CancellationToken _ct;

            public SymbolWalker(CancellationToken ct) => _ct = ct;

            /// <summary>
            /// Returns top-level symbols from the root of the parse tree
            /// (the <c>source</c> or <c>foxsource</c> rule).
            /// </summary>
            public IEnumerable<DocumentSymbol> GetTopLevelSymbols(XSharpParserRuleContext root)
            {
                // The root is the 'source' rule; its children are 'entity' rules.
                return GetChildSymbols(root);
            }

            /// <summary>
            /// Recursively collects <see cref="DocumentSymbol"/> objects for all
            /// recognised declaration nodes among <paramref name="parent"/>'s children.
            /// </summary>
            private IEnumerable<DocumentSymbol> GetChildSymbols(IParseTree parent)
            {
                var result = new List<DocumentSymbol>();

                for (int i = 0; i < parent.ChildCount; i++)
                {
                    _ct.ThrowIfCancellationRequested();

                    var child = parent.GetChild(i);
                    if (child is not XSharpParserRuleContext ctx) continue;

                    var symbol = TryBuildSymbol(ctx);
                    if (symbol != null)
                        result.Add(symbol);
                }

                return result;
            }

            /// <summary>
            /// Attempts to build a <see cref="DocumentSymbol"/> for a parse tree node.
            /// Returns <c>null</c> for nodes that are not declarations.
            /// </summary>
            private DocumentSymbol? TryBuildSymbol(XSharpParserRuleContext ctx)
            {
                return ctx switch
                {
                    // ---------------------------------------------------------
                    // Namespace
                    // ---------------------------------------------------------
                    XSharpParser.Namespace_Context ns
                        => BuildSymbol(
                            ns.Name.GetText(),
                            SymbolKind.Namespace,
                            ns,
                            GetChildSymbols(ns)),

                    // ---------------------------------------------------------
                    // Types
                    // ---------------------------------------------------------
                    XSharpParser.Class_Context cls
                        => BuildSymbol(
                            cls.Id.GetText(),
                            SymbolKind.Class,
                            cls,
                            GetChildrenFromMembers(cls._Members)),

                    XSharpParser.Interface_Context iface
                        => BuildSymbol(
                            iface.Id.GetText(),
                            SymbolKind.Interface,
                            iface,
                            GetChildrenFromMembers(iface._Members)),

                    XSharpParser.Structure_Context strct
                        => BuildSymbol(
                            strct.Id.GetText(),
                            SymbolKind.Struct,
                            strct,
                            GetChildrenFromMembers(strct._Members)),

                    XSharpParser.Enum_Context en
                        => BuildSymbol(
                            en.Id.GetText(),
                            SymbolKind.Enum,
                            en,
                            GetEnumMembers(en)),

                    XSharpParser.Delegate_Context del
                        => BuildSymbol(
                            del.Id.GetText(),
                            SymbolKind.Function,   // LSP has no Delegate kind; Function is closest
                            del,
                            children: null),

                    // VO/Vulcan STRUCT and UNION
                    XSharpParser.VostructContext vostruct
                        => BuildSymbol(
                            vostruct.Id.GetText(),
                            SymbolKind.Struct,
                            vostruct,
                            GetChildSymbols(vostruct)),

                    XSharpParser.VounionContext vounion
                        => BuildSymbol(
                            vounion.Id.GetText(),
                            SymbolKind.Struct,
                            vounion,
                            GetChildSymbols(vounion)),

                    // ---------------------------------------------------------
                    // Global functions and procedures
                    // ---------------------------------------------------------
                    XSharpParser.FuncprocContext fp
                        => BuildSymbol(
                            fp.Sig.Id.GetText(),
                            fp.T.Token.Type == XSharpParser.FUNCTION
                                ? SymbolKind.Function
                                : SymbolKind.Function,   // PROCEDURE maps to Function too
                            fp,
                            children: null),

                    // VO DEFINE constant
                    XSharpParser.VodefineContext vodef
                        => BuildSymbol(
                            vodef.Id.GetText(),
                            SymbolKind.Constant,
                            vodef,
                            children: null),

                    // VO GLOBAL variable
                    XSharpParser.VoglobalContext voglobal
                        => BuildVoGlobalSymbols(voglobal),

                    // ---------------------------------------------------------
                    // Class members — unwrap the ClassmemberContext wrapper
                    // ---------------------------------------------------------
                    XSharpParser.ClassmemberContext cm
                        => cm.ChildCount > 0 && cm.GetChild(0) is XSharpParserRuleContext inner
                            ? TryBuildSymbol(inner)
                            : null,

                    // Method / Access / Assign
                    XSharpParser.MethodContext method
                        => BuildSymbol(
                            GetMethodName(method),
                            GetMethodKind(method),
                            method,
                            children: null),

                    // Constructor
                    XSharpParser.ConstructorContext ctor
                        => BuildSymbol(
                            "Constructor",
                            SymbolKind.Constructor,
                            ctor,
                            children: null),

                    // Destructor
                    XSharpParser.DestructorContext dtor
                        => BuildSymbol(
                            "Destructor",
                            SymbolKind.Function,
                            dtor,
                            children: null),

                    // Property
                    XSharpParser.PropertyContext prop
                        => BuildSymbol(
                            prop.Id?.GetText() ?? "Item",   // indexers have no Id
                            SymbolKind.Property,
                            prop,
                            children: null),

                    // Event
                    XSharpParser.Event_Context evt
                        => BuildSymbol(
                            evt.Id.GetText(),
                            SymbolKind.Event,
                            evt,
                            children: null),

                    // Class variables / fields
                    XSharpParser.ClassvarsContext cvars
                        => BuildClassVarSymbols(cvars),

                    // Anything else — recurse in case there are nested declarations
                    _ => null
                };
            }

            // ----------------------------------------------------------------
            // Helpers
            // ----------------------------------------------------------------

            /// <summary>
            /// Converts a list of <see cref="XSharpParser.ClassmemberContext"/> items
            /// (the member wrapper used inside class/interface/struct bodies) into
            /// child <see cref="DocumentSymbol"/> objects.
            /// </summary>
            private IEnumerable<DocumentSymbol> GetChildrenFromMembers(
                IList<XSharpParser.ClassmemberContext> members)
            {
                var result = new List<DocumentSymbol>();
                if (members == null) return result;

                foreach (var cm in members)
                {
                    _ct.ThrowIfCancellationRequested();
                    var sym = TryBuildSymbol(cm);
                    if (sym != null) result.Add(sym);
                }
                return result;
            }

            /// <summary>
            /// Returns one <see cref="DocumentSymbol"/> per enum member.
            /// </summary>
            private IEnumerable<DocumentSymbol> GetEnumMembers(XSharpParser.Enum_Context en)
            {
                var result = new List<DocumentSymbol>();
                if (en._Members == null) return result;

                foreach (var m in en._Members)
                {
                    _ct.ThrowIfCancellationRequested();
                    if (m is XSharpParser.EnummemberContext em && em.Id != null)
                    {
                        result.Add(BuildSymbol(
                            em.Id.GetText(),
                            SymbolKind.EnumMember,
                            em,
                            children: null)!);
                    }
                }
                return result;
            }

            /// <summary>
            /// A VO GLOBAL declaration can have multiple variables on one line
            /// (<c>GLOBAL x, y AS STRING</c>). We build one symbol per variable
            /// and wrap them in a containing symbol when there are multiple.
            /// </summary>
            private DocumentSymbol? BuildVoGlobalSymbols(XSharpParser.VoglobalContext ctx)
            {
                if (ctx._Vars == null || ctx._Vars.Count == 0) return null;

                if (ctx._Vars.Count == 1)
                {
                    return BuildSymbol(
                        ctx._Vars[0].Id.GetText(),
                        SymbolKind.Variable,
                        ctx,
                        children: null);
                }

                // Multiple variables: create children under a group node named after the first
                var children = new List<DocumentSymbol>();
                foreach (var v in ctx._Vars)
                {
                    _ct.ThrowIfCancellationRequested();
                    children.Add(BuildSymbol(v.Id.GetText(), SymbolKind.Variable, ctx, null)!);
                }

                return BuildSymbol(ctx._Vars[0].Id.GetText(), SymbolKind.Variable, ctx, children);
            }

            /// <summary>
            /// A class variable line can have multiple variables
            /// (<c>PRIVATE _x, _y AS STRING</c>). Same grouping strategy as
            /// <see cref="BuildVoGlobalSymbols"/>.
            /// </summary>
            private DocumentSymbol? BuildClassVarSymbols(XSharpParser.ClassvarsContext ctx)
            {
                if (ctx._Vars == null || ctx._Vars.Count == 0) return null;

                if (ctx._Vars.Count == 1)
                {
                    return BuildSymbol(
                        ctx._Vars[0].Id.GetText(),
                        SymbolKind.Field,
                        ctx,
                        children: null);
                }

                var children = new List<DocumentSymbol>();
                foreach (var v in ctx._Vars)
                {
                    _ct.ThrowIfCancellationRequested();
                    children.Add(BuildSymbol(v.Id.GetText(), SymbolKind.Field, ctx, null)!);
                }

                return BuildSymbol(ctx._Vars[0].Id.GetText(), SymbolKind.Field, ctx, children);
            }

            /// <summary>
            /// Returns the display name for a <see cref="XSharpParser.MethodContext"/>,
            /// appending <c>" [Access]"</c> or <c>" [Assign]"</c> for VO-style
            /// ACCESS/ASSIGN methods so they are visually distinct in the outline.
            /// </summary>
            private static string GetMethodName(XSharpParser.MethodContext method)
            {
                string name = method.Sig?.Id?.GetText() ?? "?";
                return method.T?.Token?.Type switch
                {
                    XSharpParser.ACCESS => name + " [Access]",
                    XSharpParser.ASSIGN => name + " [Assign]",
                    _                  => name
                };
            }

            /// <summary>
            /// Returns the LSP <see cref="SymbolKind"/> for a method context.
            /// ACCESS and ASSIGN are treated as properties; everything else is a method.
            /// </summary>
            private static SymbolKind GetMethodKind(XSharpParser.MethodContext method)
            {
                return method.T?.Token?.Type switch
                {
                    XSharpParser.ACCESS => SymbolKind.Property,
                    XSharpParser.ASSIGN => SymbolKind.Property,
                    _                  => SymbolKind.Method
                };
            }

            /// <summary>
            /// Builds a <see cref="DocumentSymbol"/> from a parse tree node, computing
            /// the LSP <see cref="Range"/> from the node's start and stop tokens.
            /// </summary>
            /// <param name="name">The display name shown in the outline.</param>
            /// <param name="kind">The LSP symbol kind (controls the icon).</param>
            /// <param name="ctx">The parse tree node that defines this symbol.</param>
            /// <param name="children">Optional nested symbols.</param>
            /// <returns>
            /// A fully populated <see cref="DocumentSymbol"/>, or <c>null</c> if the
            /// node's position information is unavailable.
            /// </returns>
            private static DocumentSymbol? BuildSymbol(
                string name,
                SymbolKind kind,
                XSharpParserRuleContext ctx,
                IEnumerable<DocumentSymbol>? children)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;

                // XSharp lines are 1-based; LSP positions are 0-based.
                int startLine = Math.Max(0, ctx.Start.Line - 1);
                int startCol  = Math.Max(0, ctx.Start.Column);

                // Stop token may be null for incomplete/error nodes — fall back to start.
                int endLine = ctx.Stop != null
                    ? Math.Max(startLine, ctx.Stop.Line - 1)
                    : startLine;
                int endCol  = ctx.Stop != null
                    ? ctx.Stop.Column + (ctx.Stop.Text?.Length ?? 0)
                    : startCol;

                var range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(startLine, startCol),
                    new Position(endLine, endCol));

                // selectionRange highlights just the name token in the editor.
                // We use the same as range when we don't have a separate name token.
                var selectionRange = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(startLine, startCol),
                    new Position(startLine, startCol + name.Length));

                return new DocumentSymbol
                {
                    Name            = name,
                    Kind            = kind,
                    Range           = range,
                    SelectionRange  = selectionRange,
                    Children        = children != null
                        ? new Container<DocumentSymbol>(children)
                        : null
                };
            }
        }
    }
}
