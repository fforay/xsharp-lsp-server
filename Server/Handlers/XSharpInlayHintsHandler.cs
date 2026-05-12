using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles <c>textDocument/inlayHint</c>.
    /// <para>
    /// Walks the parse tree of the requested document and looks for
    /// <see cref="XSharpParser.MethodCallContext"/> nodes whose callee is a
    /// simple identifier.  For each such call it looks up the function /
    /// method overloads in the XSharp IntelliSense DB, extracts the parameter
    /// names from the <c>Sourcecode</c> prototype string, then emits one
    /// <see cref="InlayHint"/> of kind <see cref="InlayHintKind.Parameter"/>
    /// before each positional (unnamed) argument.
    /// </para>
    /// <para>
    /// Hints are only emitted when the DB lookup succeeds and the prototype
    /// contains at least as many parameters as the call has arguments.  All
    /// failures degrade gracefully to an empty result.
    /// </para>
    /// </summary>
    public class XSharpInlayHintsHandler : InlayHintsHandlerBase
    {
        private readonly XSharpDocumentService           _documentService;
        private readonly XSharpDatabaseService           _databaseService;
        private readonly ILogger<XSharpInlayHintsHandler> _logger;

        // Matches a single parameter declaration inside a prototype string:
        //   foo AS INT, bar AS STRING, baz, ...
        // Captures group 1 = parameter name.
        private static readonly Regex _paramRegex = new Regex(
            @"(?:^|,)\s*(?:REF\s+|OUT\s+|PARAMS\s+)?(\w+)(?:\s+AS\s+\w[\w.]*(?:\[\])?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public XSharpInlayHintsHandler(
            XSharpDocumentService            documentService,
            XSharpDatabaseService            databaseService,
            ILogger<XSharpInlayHintsHandler> logger)
        {
            _documentService = documentService;
            _databaseService = databaseService;
            _logger          = logger;
        }

        // ----------------------------------------------------------------
        // Registration
        // ----------------------------------------------------------------
        protected override InlayHintRegistrationOptions CreateRegistrationOptions(
            InlayHintClientCapabilities capability,
            ClientCapabilities          clientCapabilities)
            => new InlayHintRegistrationOptions
            {
                DocumentSelector  = TextDocumentSelector.ForLanguage("xsharp"),
                ResolveProvider   = false,
            };

        // ----------------------------------------------------------------
        // Resolve (no-op — ResolveProvider = false)
        // ----------------------------------------------------------------
        public override Task<InlayHint> Handle(
            InlayHint         request,
            CancellationToken cancellationToken)
            => Task.FromResult(request);

        // ----------------------------------------------------------------
        // Handle
        // ----------------------------------------------------------------
        public override Task<InlayHintContainer?> Handle(
            InlayHintParams   request,
            CancellationToken cancellationToken)
        {
            try
            {
                var uri = request.TextDocument.Uri;

                if (!_documentService.TryGetParsed(uri, out var parsed) || parsed.Tree == null)
                    return Task.FromResult<InlayHintContainer?>(null);

                var hints = new List<InlayHint>();
                CollectHints(parsed.Tree, hints, request.Range, cancellationToken);

                _logger.LogInformation(
                    "InlayHints: {Count} hint(s) for {Uri}", hints.Count, uri);

                return Task.FromResult<InlayHintContainer?>(
                    hints.Count > 0 ? new InlayHintContainer(hints) : null);
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult<InlayHintContainer?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InlayHints failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<InlayHintContainer?>(null);
            }
        }

        // ----------------------------------------------------------------
        // Parse tree walker
        // ----------------------------------------------------------------

        private void CollectHints(
            IParseTree        node,
            List<InlayHint>   hints,
            OmniSharp.Extensions.LanguageServer.Protocol.Models.Range visibleRange,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (node is XSharpParser.MethodCallContext mc)
            {
                TryEmitHints(mc, hints, visibleRange);
            }

            for (int i = 0; i < node.ChildCount; i++)
                CollectHints(node.GetChild(i), hints, visibleRange, ct);
        }

        private void TryEmitHints(
            XSharpParser.MethodCallContext mc,
            List<InlayHint>                hints,
            OmniSharp.Extensions.LanguageServer.Protocol.Models.Range visibleRange)
        {
            try
            {
                // The callee expression (e.g. "MyFunc" or "obj:Method").
                var calleeText = mc.expression()?.GetText();
                if (string.IsNullOrEmpty(calleeText)) return;

                // Extract simple name (last segment after ':' or '.')
                var simpleName = SimpleName(calleeText);
                if (string.IsNullOrEmpty(simpleName)) return;

                // Argument list
                var argList = mc.argumentList();
                if (argList == null) return;

                var args = argList.namedArgument();
                if (args == null || args.Length == 0) return;

                // Only process calls where the first arg's start token falls
                // within the requested visible range (avoid doing work for
                // off-screen calls).
                int callLine = Math.Max(0, mc.Start.Line - 1);
                if (callLine < visibleRange.Start.Line || callLine > visibleRange.End.Line)
                    return;

                // Look up parameter names from DB.
                var paramNames = GetParamNames(simpleName);
                if (paramNames.Count == 0) return;

                // Emit one hint per unnamed argument, up to the number of
                // known parameters.
                int limit = Math.Min(args.Length, paramNames.Count);
                for (int i = 0; i < limit; i++)
                {
                    var arg = args[i];

                    // Skip if the argument already uses a named syntax (NamedArgumentContext)
                    if (arg is XSharpParser.NamedArgumentContext) continue;

                    var startTok = arg.Start;
                    if (startTok == null) continue;

                    int line = Math.Max(0, startTok.Line - 1);
                    int col  = startTok.Column;

                    hints.Add(new InlayHint
                    {
                        Position    = new Position(line, col),
                        Label       = new StringOrInlayHintLabelParts($"{paramNames[i]}:"),
                        Kind        = InlayHintKind.Parameter,
                        PaddingRight = true,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "InlayHint: skipped call node");
            }
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Looks up the first matching overload in the DB and returns an
        /// ordered list of parameter names parsed from its <c>Sourcecode</c>
        /// prototype.  Returns an empty list on any failure.
        /// </summary>
        private List<string> GetParamNames(string funcName)
        {
            var overloads = _databaseService.FindOverloads(funcName);
            foreach (var sym in overloads)
            {
                var names = ParseParamNames(sym.Sourcecode);
                if (names.Count > 0) return names;
            }
            return new List<string>();
        }

        /// <summary>
        /// Parses parameter names out of an XSharp prototype string such as
        /// <c>FUNCTION Foo(x AS INT, y AS STRING) AS VOID</c>.
        /// </summary>
        internal static List<string> ParseParamNames(string? sourcecode)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(sourcecode)) return result;

            // Extract the content between the first '(' and the matching ')'
            int open  = sourcecode.IndexOf('(');
            int close = sourcecode.LastIndexOf(')');
            if (open < 0 || close <= open) return result;

            string paramsPart = sourcecode.Substring(open + 1, close - open - 1).Trim();
            if (string.IsNullOrEmpty(paramsPart)) return result;

            foreach (Match m in _paramRegex.Matches(paramsPart))
            {
                var name = m.Groups[1].Value;
                // Skip keywords that appear in modifier positions (e.g. "AS", "REF")
                if (string.Equals(name, "AS",     StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "REF",    StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "OUT",    StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "PARAMS", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(name);
            }
            return result;
        }

        /// <summary>
        /// Returns the last simple identifier segment from a possibly-qualified
        /// expression such as <c>obj:Method</c> or <c>Ns.Class.Method</c>.
        /// </summary>
        private static string SimpleName(string expr)
        {
            int colon = expr.LastIndexOf(':');
            int dot   = expr.LastIndexOf('.');
            int sep   = Math.Max(colon, dot);
            return sep >= 0 ? expr.Substring(sep + 1) : expr;
        }
    }
}
