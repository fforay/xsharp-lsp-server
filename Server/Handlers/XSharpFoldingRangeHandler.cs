using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using LanguageService.SyntaxTree.Misc;
using LanguageService.SyntaxTree.Tree;
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
    /// Handles the <c>textDocument/foldingRange</c> LSP request.
    /// <para>
    /// Returns fold ranges derived from three sources:
    /// <list type="number">
    ///   <item>
    ///     <b>Parse-tree nodes</b> — namespaces, types (class, interface, struct, enum,
    ///     VO struct/union), functions, procedures, methods, constructors, destructors,
    ///     properties, and events all produce a <c>region</c> fold range spanning the
    ///     full extent of their declaration.
    ///   </item>
    ///   <item>
    ///     <b><c>#region</c> / <c>#endregion</c> preprocessor pairs</b> — scanned from
    ///     the token stream (tokens <c>PP_REGION</c> / <c>PP_ENDREGION</c>).  The fold
    ///     label shown by the editor is the text following <c>#region</c> on the same
    ///     line (if present).
    ///   </item>
    ///   <item>
    ///     <b>Multi-line block comments</b> — <c>ML_COMMENT</c> and <c>DOC_COMMENT</c>
    ///     tokens that span more than one line produce a <c>comment</c> fold range.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    public class XSharpFoldingRangeHandler : FoldingRangeHandlerBase
    {
        private readonly XSharpDocumentService _documentService;
        private readonly ILogger<XSharpFoldingRangeHandler> _logger;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpFoldingRangeHandler(
            XSharpDocumentService documentService,
            ILogger<XSharpFoldingRangeHandler> logger)
        {
            _documentService = documentService;
            _logger          = logger;
        }

        /// <summary>
        /// Registers this handler for the <c>"xsharp"</c> language.
        /// </summary>
        protected override FoldingRangeRegistrationOptions CreateRegistrationOptions(
            FoldingRangeCapability capability,
            ClientCapabilities clientCapabilities)
        {
            return new FoldingRangeRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };
        }

        /// <summary>
        /// Entry point for <c>textDocument/foldingRange</c> requests.
        /// </summary>
        public override Task<Container<FoldingRange>?> Handle(
            FoldingRangeRequestParam request,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!_documentService.TryGetParsed(request.TextDocument.Uri, out var parsed))
                {
                    _logger.LogWarning("No parse result for {Uri}", request.TextDocument.Uri);
                    return Task.FromResult<Container<FoldingRange>?>(null);
                }

                var ranges = new List<FoldingRange>();

                // 1. Parse-tree structural folds
                if (parsed.Tree != null)
                    CollectTreeFolds(parsed.Tree, ranges, cancellationToken);

                // 2. Token-stream folds (#region / block comments)
                if (parsed.TokenStream != null)
                    CollectTokenFolds(parsed.TokenStream, ranges, cancellationToken);

                _logger.LogInformation(
                    "FoldingRange: {Count} range(s) for {Uri}",
                    ranges.Count, request.TextDocument.Uri);

                return Task.FromResult<Container<FoldingRange>?>(new Container<FoldingRange>(ranges));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult<Container<FoldingRange>?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FoldingRange failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<Container<FoldingRange>?>(null);
            }
        }

        // ====================================================================
        // Parse-tree structural folds
        // ====================================================================

        /// <summary>
        /// Walks the parse tree recursively and emits a <c>region</c> fold range
        /// for every node that represents a multi-line declaration block.
        /// </summary>
        private static void CollectTreeFolds(
            IParseTree node,
            List<FoldingRange> ranges,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (node is XSharpParserRuleContext ctx)
            {
                if (IsBlockNode(ctx))
                {
                    var range = TryMakeFold(ctx, FoldingRangeKind.Region);
                    if (range != null)
                        ranges.Add(range);
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
                CollectTreeFolds(node.GetChild(i), ranges, ct);
        }

        /// <summary>
        /// Returns <c>true</c> for parse tree node types that represent declaration
        /// blocks (i.e. nodes that have a meaningful start line and end line).
        /// </summary>
        private static bool IsBlockNode(XSharpParserRuleContext ctx)
        {
            return ctx is XSharpParser.Namespace_Context
                       or XSharpParser.Class_Context
                       or XSharpParser.Interface_Context
                       or XSharpParser.Structure_Context
                       or XSharpParser.Enum_Context
                       or XSharpParser.Delegate_Context
                       or XSharpParser.VostructContext
                       or XSharpParser.VounionContext
                       or XSharpParser.FuncprocContext
                       or XSharpParser.MethodContext
                       or XSharpParser.ConstructorContext
                       or XSharpParser.DestructorContext
                       or XSharpParser.PropertyContext
                       or XSharpParser.Event_Context;
        }

        /// <summary>
        /// Builds a <see cref="FoldingRange"/> from a parse tree node.
        /// Returns <c>null</c> if the node spans only a single line (nothing to fold)
        /// or if position information is unavailable.
        /// </summary>
        private static FoldingRange? TryMakeFold(
            XSharpParserRuleContext ctx,
            FoldingRangeKind kind)
        {
            if (ctx.Start == null || ctx.Stop == null) return null;

            // XSharp lines are 1-based; LSP lines are 0-based.
            int startLine = ctx.Start.Line - 1;
            int endLine   = ctx.Stop.Line  - 1;

            if (endLine <= startLine) return null;   // single-line node — nothing to fold

            return new FoldingRange
            {
                StartLine   = startLine,
                StartCharacter = ctx.Start.Column,
                EndLine     = endLine,
                EndCharacter   = ctx.Stop.Column + (ctx.Stop.Text?.Length ?? 0),
                Kind        = kind,
            };
        }

        // ====================================================================
        // Token-stream folds (#region / block comments)
        // ====================================================================

        /// <summary>
        /// Scans the full token stream (all channels) and emits:
        /// <list type="bullet">
        ///   <item>
        ///     A <c>region</c> fold for each matched <c>#region</c> / <c>#endregion</c> pair.
        ///   </item>
        ///   <item>
        ///     A <c>comment</c> fold for each multi-line <c>ML_COMMENT</c> or
        ///     <c>DOC_COMMENT</c> token.
        ///   </item>
        /// </list>
        /// </summary>
        private static void CollectTokenFolds(
            ITokenStream tokenStream,
            List<FoldingRange> ranges,
            CancellationToken ct)
        {
            if (tokenStream is not BufferedTokenStream buffered) return;

            var tokens = buffered.GetTokens();
            if (tokens == null) return;

            // Stack of (startLine, label?) for nested #region directives.
            var regionStack = new Stack<(int startLine, string? label)>();

            foreach (var tok in tokens)
            {
                ct.ThrowIfCancellationRequested();

                if (tok == null || tok.Type == -1 /* EOF */) continue;

                switch (tok.Type)
                {
                    // ----------------------------------------------------------
                    // #region — push onto stack; label is text after the keyword.
                    // ----------------------------------------------------------
                    case XSharpParser.PP_REGION:
                    {
                        // The preprocessor token text may be just "#region" or
                        // "#region <label>".  We extract the label part (if any).
                        string? label = ExtractRegionLabel(tok.Text);
                        regionStack.Push((tok.Line - 1, label));
                        break;
                    }

                    // ----------------------------------------------------------
                    // #endregion — pop and emit a fold range.
                    // ----------------------------------------------------------
                    case XSharpParser.PP_ENDREGION:
                    {
                        if (regionStack.Count > 0)
                        {
                            var (startLine, label) = regionStack.Pop();
                            int endLine = tok.Line - 1;

                            if (endLine > startLine)
                            {
                                ranges.Add(new FoldingRange
                                {
                                    StartLine = startLine,
                                    EndLine   = endLine,
                                    Kind      = FoldingRangeKind.Region,
                                    CollapsedText = label,
                                });
                            }
                        }
                        break;
                    }

                    // ----------------------------------------------------------
                    // Multi-line block comments (/** ... */ and /* ... */)
                    // ----------------------------------------------------------
                    case XSharpParser.ML_COMMENT:
                    case XSharpParser.DOC_COMMENT:
                    {
                        int startLine = tok.Line - 1;
                        // Count newlines inside the token text to find the end line.
                        int lineCount = CountNewlines(tok.Text);
                        if (lineCount > 0)
                        {
                            ranges.Add(new FoldingRange
                            {
                                StartLine = startLine,
                                EndLine   = startLine + lineCount,
                                Kind      = FoldingRangeKind.Comment,
                            });
                        }
                        break;
                    }
                }
            }

            // Any unmatched #region directives at end-of-file are silently discarded.
        }

        // ====================================================================
        // Utilities
        // ====================================================================

        /// <summary>
        /// Extracts an optional label from the text of a <c>#region</c> token.
        /// <para>
        /// Examples:
        /// <list type="bullet">
        ///   <item><c>"#region"</c>          → <c>null</c></item>
        ///   <item><c>"#region My Block"</c> → <c>"My Block"</c></item>
        /// </list>
        /// </para>
        /// </summary>
        private static string? ExtractRegionLabel(string? tokenText)
        {
            if (string.IsNullOrWhiteSpace(tokenText)) return null;

            // Token text is typically "#region" or "#region SomeName".
            // Find the first whitespace after "#region" and return the rest.
            int idx = tokenText.IndexOf("region", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int afterKeyword = idx + "region".Length;
            if (afterKeyword >= tokenText.Length) return null;

            string rest = tokenText.Substring(afterKeyword).Trim();
            return string.IsNullOrEmpty(rest) ? null : rest;
        }

        /// <summary>
        /// Counts the number of newline sequences (<c>\n</c>) inside a token's text.
        /// Used to determine how many additional lines a multi-line comment spans.
        /// </summary>
        private static int CountNewlines(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int count = 0;
            foreach (char c in text)
                if (c == '\n') count++;
            return count;
        }
    }
}
