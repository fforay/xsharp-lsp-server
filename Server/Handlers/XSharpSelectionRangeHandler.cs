using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>textDocument/selectionRange</c> LSP request — smart
    /// expand / shrink selection (Alt+Shift+→ / Alt+Shift+← in VS Code).
    /// <para>
    /// For each cursor position the handler walks the parse tree and collects
    /// every node that contains that position, ordered from innermost to
    /// outermost.  The result is a <see cref="SelectionRange"/> linked list
    /// where <c>Parent</c> always points to the next larger enclosing range,
    /// allowing the client to expand the selection one syntactic unit at a time.
    /// </para>
    /// </summary>
    public class XSharpSelectionRangeHandler : SelectionRangeHandlerBase
    {
        private readonly XSharpDocumentService _documentService;
        private readonly ILogger<XSharpSelectionRangeHandler> _logger;

        public XSharpSelectionRangeHandler(
            XSharpDocumentService documentService,
            ILogger<XSharpSelectionRangeHandler> logger)
        {
            _documentService = documentService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override SelectionRangeRegistrationOptions CreateRegistrationOptions(
            SelectionRangeCapability capability,
            ClientCapabilities clientCapabilities)
            => new SelectionRangeRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        /// <inheritdoc/>
        public override Task<Container<SelectionRange>?> Handle(
            SelectionRangeParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!_documentService.TryGetParsed(request.TextDocument.Uri, out var parsed)
                    || parsed.Tree == null)
                    return Task.FromResult<Container<SelectionRange>?>(null);

                var results = new List<SelectionRange>();

                foreach (var position in request.Positions)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chain = BuildChain(parsed.Tree, position);
                    if (chain != null)
                        results.Add(chain);
                }

                _logger.LogInformation(
                    "SelectionRange: {Count} range chain(s) for {Uri}",
                    results.Count, request.TextDocument.Uri);

                return Task.FromResult<Container<SelectionRange>?>(
                    results.Count > 0 ? new Container<SelectionRange>(results) : null);
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult<Container<SelectionRange>?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SelectionRange failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<Container<SelectionRange>?>(null);
            }
        }

        // ====================================================================
        // Chain builder
        // ====================================================================

        /// <summary>
        /// Collects all parse-tree nodes that contain <paramref name="cursor"/>,
        /// deduplicates adjacent nodes with the same range, then builds a
        /// <see cref="SelectionRange"/> linked list from outermost to innermost
        /// so that successive <c>Parent</c> traversals expand the selection.
        /// </summary>
        private static SelectionRange? BuildChain(XSharpParserRuleContext root, Position cursor)
        {
            // Collect nodes that contain the cursor, outermost first.
            var nodes = new List<XSharpParserRuleContext>();
            CollectContaining(root, cursor, nodes);

            if (nodes.Count == 0) return null;

            // Build the linked list: iterate outermost→innermost, prepending
            // each new node so the returned head is the innermost selection
            // with Parent pointing progressively outward.
            SelectionRange? chain = null;
            LspRange? lastRange  = null;

            foreach (var node in nodes)
            {
                var range = NodeToRange(node);

                // Skip nodes whose range is identical to the previous one —
                // they don't add a useful expansion step for the user.
                if (lastRange != null && RangesEqual(range, lastRange))
                    continue;

                chain     = new SelectionRange { Range = range, Parent = chain! };
                lastRange = range;
            }

            // chain is currently outermost (head=outermost, tail=innermost via Parent).
            // LSP expects head=innermost. Reverse the chain.
            return ReverseChain(chain);
        }

        // ====================================================================
        // Tree walker
        // ====================================================================

        private static void CollectContaining(
            IParseTree node,
            Position cursor,
            List<XSharpParserRuleContext> results)
        {
            if (node is not XSharpParserRuleContext ctx) return;
            if (!ContainsCursor(ctx, cursor)) return;

            results.Add(ctx);

            for (int i = 0; i < node.ChildCount; i++)
                CollectContaining(node.GetChild(i), cursor, results);
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static bool ContainsCursor(XSharpParserRuleContext ctx, Position cursor)
        {
            if (ctx.Start == null) return false;
            int startLine = Math.Max(0, ctx.Start.Line - 1);
            int stopLine  = ctx.Stop != null ? Math.Max(0, ctx.Stop.Line - 1) : startLine;
            return cursor.Line >= startLine && cursor.Line <= stopLine;
        }

        private static LspRange NodeToRange(XSharpParserRuleContext ctx)
        {
            int startLine = Math.Max(0, ctx.Start.Line - 1);
            int startCol  = Math.Max(0, ctx.Start.Column);

            int endLine, endCol;
            if (ctx.Stop != null)
            {
                endLine = Math.Max(0, ctx.Stop.Line - 1);
                // End column is exclusive: stop past the last character of the token.
                endCol  = Math.Max(0, ctx.Stop.Column + (ctx.Stop.Text?.Length ?? 0));
            }
            else
            {
                endLine = startLine;
                endCol  = startCol;
            }

            return new LspRange(new Position(startLine, startCol), new Position(endLine, endCol));
        }

        private static bool RangesEqual(LspRange a, LspRange b)
            => a.Start.Line      == b.Start.Line
            && a.Start.Character == b.Start.Character
            && a.End.Line        == b.End.Line
            && a.End.Character   == b.End.Character;

        /// <summary>
        /// Reverses the <c>Parent</c> chain so the returned head is the innermost
        /// (smallest) range, with <c>Parent</c> pointing toward larger ranges.
        /// </summary>
        private static SelectionRange? ReverseChain(SelectionRange? head)
        {
            SelectionRange? prev    = null;
            SelectionRange? current = head;

            while (current != null)
            {
                var next    = current.Parent;
                var node    = new SelectionRange { Range = current.Range, Parent = prev! };
                prev        = node;
                current     = next;
            }

            return prev;
        }
    }
}
