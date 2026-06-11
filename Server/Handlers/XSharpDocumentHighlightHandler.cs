using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles <c>textDocument/documentHighlight</c>.
    /// <para>
    /// Two modes depending on the token under the cursor:
    /// <list type="bullet">
    ///   <item><b>Structural keyword</b> (IF, FOR, WHILE, CLASS, …) — highlights every
    ///     boundary keyword in the enclosing structural block (opener, any intermediates,
    ///     and closer). Implemented via parse-tree walk.</item>
    ///   <item><b>Identifier</b> — highlights every occurrence of the word in the current
    ///     file, using the live document scan with workspace-index fallback.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class XSharpDocumentHighlightHandler : DocumentHighlightHandlerBase
    {
        private readonly XSharpDocumentService                      _documentService;
        private readonly XSharpWorkspaceIndex                       _workspaceIndex;
        private readonly ILogger<XSharpDocumentHighlightHandler>    _logger;

        // Structural keywords that trigger keyword-pair highlighting instead of identifier
        // highlighting. Case-insensitive.
        private static readonly HashSet<string> s_structuralKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "IF", "ELSEIF", "ELSE", "ENDIF",
            "FOR", "NEXT", "FOREACH",
            "WHILE", "ENDDO", "DO",
            "REPEAT", "UNTIL",
            "CASE", "OTHERWISE", "ENDCASE",
            "SWITCH",
            "TRY", "CATCH", "FINALLY", "ENDTRY",
            "BEGIN", "SEQUENCE", "RECOVER", "ENDSEQUENCE",
            "CLASS", "ENDCLASS",
            "INTERFACE", "STRUCTURE", "ENUM",
            "NAMESPACE",
            "WITH", "ENDWITH",
            "VOSTRUCT", "UNION",
            "END",
        };

        public XSharpDocumentHighlightHandler(
            XSharpDocumentService                   documentService,
            XSharpWorkspaceIndex                    workspaceIndex,
            ILogger<XSharpDocumentHighlightHandler> logger)
        {
            _documentService = documentService;
            _workspaceIndex  = workspaceIndex;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
            DocumentHighlightCapability capability,
            ClientCapabilities          clientCapabilities)
            => new DocumentHighlightRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        /// <inheritdoc/>
        public override Task<DocumentHighlightContainer?> Handle(
            DocumentHighlightParams request,
            CancellationToken       cancellationToken)
        {
            try
            {
                if (!_documentService.TryGetText(request.TextDocument.Uri, out var text))
                    return Task.FromResult<DocumentHighlightContainer?>(null);

                string word = XSharpReferencesHandler.ExtractWord(text, request.Position);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<DocumentHighlightContainer?>(null);

                _logger.LogInformation("DocumentHighlight: '{Word}' in {Uri}", word, request.TextDocument.Uri);

                // ── Structural-keyword pair highlighting ──────────────────────────
                if (s_structuralKeywords.Contains(word))
                {
                    var kwHighlights = TryKeywordPairHighlights(request.TextDocument.Uri, request.Position);
                    _logger.LogInformation(
                        "DocumentHighlight (keyword pair): {Count} token(s) for '{Word}'",
                        kwHighlights.Count, word);
                    return Task.FromResult<DocumentHighlightContainer?>(
                        kwHighlights.Count > 0
                            ? new DocumentHighlightContainer(kwHighlights)
                            : null);
                }

                // ── Identifier occurrence highlighting ────────────────────────────
                var highlights = new Dictionary<int, DocumentHighlight>();

                bool foundInOpen = false;
                foreach (var (uri, line, col, len) in _documentService.FindTokenLocations(word))
                {
                    if (uri != request.TextDocument.Uri) continue;
                    foundInOpen = true;
                    highlights[line] = new DocumentHighlight
                    {
                        Kind  = DocumentHighlightKind.Text,
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(line, col),
                            new Position(line, col + len)),
                    };
                }

                if (!foundInOpen)
                {
                    string? filePath = request.TextDocument.Uri.GetFileSystemPath();
                    if (filePath != null)
                    {
                        foreach (var tok in _workspaceIndex.FindTokenLocations(word))
                        {
                            if (!string.Equals(tok.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                                continue;
                            highlights.TryAdd(tok.Line, new DocumentHighlight
                            {
                                Kind  = DocumentHighlightKind.Text,
                                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                    new Position(tok.Line, tok.Col),
                                    new Position(tok.Line, tok.Col + tok.Text.Length)),
                            });
                        }
                    }
                }

                _logger.LogInformation(
                    "DocumentHighlight: {Count} occurrence(s) of '{Word}'", highlights.Count, word);

                return Task.FromResult<DocumentHighlightContainer?>(
                    highlights.Count > 0
                        ? new DocumentHighlightContainer(highlights.Values)
                        : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DocumentHighlight failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<DocumentHighlightContainer?>(null);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Keyword-pair highlighting
        // ─────────────────────────────────────────────────────────────────────────

        private List<DocumentHighlight> TryKeywordPairHighlights(DocumentUri uri, Position cursor)
        {
            if (!_documentService.TryGetParsed(uri, out var parsed)) return [];
            if (parsed.Tree == null)                                   return [];
            if (parsed.TokenStream is not BufferedTokenStream stream)  return [];

            stream.Fill();
            var allTokens = stream.GetTokens();
            if (allTokens == null) return [];

            int cursorLine0 = cursor.Line;

            XSharpParserRuleContext? ctx = null;
            FindInnermostStructuralContext(parsed.Tree, cursorLine0, ref ctx);
            if (ctx == null) return [];

            var positions = new List<(int line0, int col, int len)>();
            CollectBoundaryTokens(positions, ctx, allTokens);
            if (positions.Count == 0) return [];

            return positions
                .Where(p => p.len > 0)
                .Select(p => new DocumentHighlight
                {
                    Kind  = DocumentHighlightKind.Write,   // Write = strong highlight (keyword pair)
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(p.line0, p.col),
                        new Position(p.line0, p.col + p.len)),
                })
                .ToList();
        }

        // Walks the parse tree depth-first, keeping the innermost (deepest) structural
        // context whose line range contains cursorLine0 (0-based).
        private static void FindInnermostStructuralContext(
            IParseTree node,
            int        cursorLine0,
            ref        XSharpParserRuleContext? result)
        {
            if (node is not XSharpParserRuleContext ctx) return;

            int startLine0 = (ctx.Start?.Line ?? 1) - 1;
            int stopLine0  = (ctx.Stop?.Line  ?? startLine0 + 1) - 1;

            if (cursorLine0 < startLine0 || cursorLine0 > stopLine0) return;

            if (IsStructuralContext(ctx))
                result = ctx;  // keep innermost — later children override

            for (int i = 0; i < node.ChildCount; i++)
                FindInnermostStructuralContext(node.GetChild(i), cursorLine0, ref result);
        }

        private static bool IsStructuralContext(XSharpParserRuleContext ctx)
            => ctx is XSharpParser.IfStmtContext
                   or XSharpParser.ForStmtContext
                   or XSharpParser.ForeachStmtContext
                   or XSharpParser.WhileStmtContext
                   or XSharpParser.RepeatStmtContext
                   or XSharpParser.CaseStmtContext
                   or XSharpParser.SwitchStmtContext
                   or XSharpParser.TryStmtContext
                   or XSharpParser.SeqStmtContext
                   or XSharpParser.Class_Context
                   or XSharpParser.Interface_Context
                   or XSharpParser.Structure_Context
                   or XSharpParser.Enum_Context
                   or XSharpParser.Namespace_Context
                   or XSharpParser.VostructContext
                   or XSharpParser.VounionContext
                   or XSharpParser.WithBlockContext;

        // Collects the (line0, col, len) of every structural boundary keyword in ctx.
        private static void CollectBoundaryTokens(
            List<(int line0, int col, int len)> result,
            XSharpParserRuleContext              ctx,
            IList<IToken>                       allTokens)
        {
            // Add a single IToken (if non-null, visible, real keyword).
            void Add(IToken? tok)
            {
                if (tok == null || tok.Channel != 0) return;
                if (tok.Type == XSharpParser.EOS || tok.Type <= 0) return;
                result.Add((tok.Line - 1, tok.Column, tok.Text?.Length ?? 0));
            }

            // Add all visible tokens on ctx.Stop's line whose type is in `types`.
            void AddOnClosingLine(params int[] types)
            {
                int closingLine1 = ctx.Stop?.Line ?? -1;
                if (closingLine1 <= 0) return;
                var typeSet = new HashSet<int>(types);
                foreach (var tok in allTokens)
                {
                    if (tok.Line   != closingLine1) continue;
                    if (tok.Channel != 0)           continue;
                    if (typeSet.Contains(tok.Type))
                        result.Add((tok.Line - 1, tok.Column, tok.Text?.Length ?? 0));
                }
            }

            // Add all visible tokens on a specific 1-based line whose type is in `types`.
            void AddOnLine(int line1, params int[] types)
            {
                if (line1 <= 0) return;
                var typeSet = new HashSet<int>(types);
                foreach (var tok in allTokens)
                {
                    if (tok.Line   != line1) continue;
                    if (tok.Channel != 0)    continue;
                    if (typeSet.Contains(tok.Type))
                        result.Add((tok.Line - 1, tok.Column, tok.Text?.Length ?? 0));
                }
            }

            // Add all visible tokens with `types` within the context's 1-based line range.
            void AddInRange(params int[] types)
            {
                int startLine1 = ctx.Start?.Line ?? 1;
                int stopLine1  = ctx.Stop?.Line  ?? startLine1;
                var typeSet    = new HashSet<int>(types);
                foreach (var tok in allTokens)
                {
                    if (tok.Line < startLine1 || tok.Line > stopLine1) continue;
                    if (tok.Channel != 0)                               continue;
                    if (typeSet.Contains(tok.Type))
                        result.Add((tok.Line - 1, tok.Column, tok.Text?.Length ?? 0));
                }
            }

            switch (ctx)
            {
                // ── IF / ELSEIF / ELSE / ENDIF ────────────────────────────────────
                case XSharpParser.IfStmtContext ifCtx:
                    Add(ifCtx.i);   // IF
                    foreach (var block in ifCtx._IfBlocks ?? [])
                        if (block.st?.Type == XSharpParser.ELSEIF)
                            Add(block.st);  // ELSEIF (skip index 0, which is the main IF)
                    Add(ifCtx.el);  // ELSE (null when absent)
                    // Closer: ENDIF (single token) or END IF (two tokens)
                    Add(ifCtx.e);
                    if (ifCtx.e?.Type == XSharpParser.END)
                        AddOnClosingLine(XSharpParser.IF);
                    break;

                // ── FOR / NEXT ────────────────────────────────────────────────────
                case XSharpParser.ForStmtContext forCtx:
                    Add(forCtx.f);  // FOR
                    Add(forCtx.e);  // NEXT
                    break;

                // ── FOREACH / NEXT ────────────────────────────────────────────────
                case XSharpParser.ForeachStmtContext foreachCtx:
                    Add(foreachCtx.f);  // FOREACH
                    Add(foreachCtx.e);  // NEXT
                    break;

                // ── [DO] WHILE / ENDDO ───────────────────────────────────────────
                case XSharpParser.WhileStmtContext whileCtx:
                    if (whileCtx.Start?.Type == XSharpParser.DO)
                        Add(whileCtx.Start);  // DO (only in DO WHILE form)
                    Add(whileCtx.w);  // WHILE
                    Add(whileCtx.e);  // ENDDO
                    break;

                // ── REPEAT / UNTIL ────────────────────────────────────────────────
                case XSharpParser.RepeatStmtContext repeatCtx:
                    Add(repeatCtx.r);                          // REPEAT
                    AddOnClosingLine(XSharpParser.UNTIL);      // UNTIL (on closing line)
                    break;

                // ── DO CASE / CASE / OTHERWISE / ENDCASE ─────────────────────────
                case XSharpParser.CaseStmtContext caseCtx:
                    // DO opener + CASE of "DO CASE" header
                    if (caseCtx.Start?.Type == XSharpParser.DO)
                    {
                        Add(caseCtx.Start);
                        AddOnLine(caseCtx.Start.Line, XSharpParser.CASE);
                    }
                    // Each CASE block keyword inside the DO CASE
                    foreach (var block in caseCtx._CaseBlocks ?? [])
                        Add(block.st);
                    Add(caseCtx.oth);  // OTHERWISE (null when absent)
                    Add(caseCtx.e);    // ENDCASE
                    break;

                // ── SWITCH / CASE / OTHERWISE / END SWITCH ────────────────────────
                case XSharpParser.SwitchStmtContext switchCtx:
                    Add(switchCtx.S);  // SWITCH
                    foreach (var block in switchCtx._SwitchBlock ?? [])
                        Add(block.Key ?? block.Start);  // CASE or OTHERWISE
                    AddOnClosingLine(XSharpParser.END, XSharpParser.SWITCH);
                    break;

                // ── TRY / CATCH / FINALLY / ENDTRY ───────────────────────────────
                case XSharpParser.TryStmtContext tryCtx:
                    Add(tryCtx.T);  // TRY
                    foreach (var block in tryCtx._CatchBlock ?? [])
                        Add(block.Start);  // CATCH (W field is for WHEN clause, use Start)
                    Add(tryCtx.F);  // FINALLY (null when absent)
                    // Closer: ENDTRY (single) or END TRY (two tokens)
                    Add(tryCtx.e);
                    if (tryCtx.e?.Type == XSharpParser.END)
                        AddOnClosingLine(XSharpParser.TRY);
                    break;

                // ── BEGIN SEQUENCE / RECOVER / END SEQUENCE ───────────────────────
                case XSharpParser.SeqStmtContext seqCtx:
                    if (seqCtx.Start?.Type == XSharpParser.BEGIN)
                    {
                        Add(seqCtx.Start);
                        AddOnLine(seqCtx.Start.Line, XSharpParser.SEQUENCE);
                    }
                    AddInRange(XSharpParser.RECOVER);  // RECOVER (not in named field)
                    // Closer: ENDSEQUENCE (single) or END SEQUENCE (two tokens)
                    Add(seqCtx.e);
                    if (seqCtx.e?.Type == XSharpParser.END)
                        AddOnClosingLine(XSharpParser.SEQUENCE);
                    break;

                // ── CLASS / ENDCLASS ──────────────────────────────────────────────
                case XSharpParser.Class_Context classCtx:
                    Add(classCtx.C);  // CLASS
                    AddOnClosingLine(XSharpParser.ENDCLASS, XSharpParser.END, XSharpParser.CLASS);
                    break;

                // ── INTERFACE / END INTERFACE ─────────────────────────────────────
                case XSharpParser.Interface_Context ifaceCtx:
                    Add(ifaceCtx.I);  // INTERFACE
                    AddOnClosingLine(XSharpParser.END, XSharpParser.INTERFACE);
                    break;

                // ── STRUCTURE / END STRUCTURE ─────────────────────────────────────
                case XSharpParser.Structure_Context structCtx:
                    Add(structCtx.S);  // STRUCTURE
                    AddOnClosingLine(XSharpParser.END, XSharpParser.STRUCTURE);
                    break;

                // ── ENUM / END ENUM ───────────────────────────────────────────────
                case XSharpParser.Enum_Context enumCtx:
                    Add(enumCtx.E);  // ENUM
                    AddOnClosingLine(XSharpParser.END, XSharpParser.ENUM);
                    break;

                // ── BEGIN NAMESPACE / END NAMESPACE ───────────────────────────────
                case XSharpParser.Namespace_Context nsCtx:
                    if (nsCtx.Start != null)
                    {
                        Add(nsCtx.Start);  // BEGIN
                        AddOnLine(nsCtx.Start.Line, XSharpParser.NAMESPACE);
                    }
                    AddOnClosingLine(XSharpParser.END, XSharpParser.NAMESPACE);
                    break;

                // ── VOSTRUCT / END VOSTRUCT ───────────────────────────────────────
                case XSharpParser.VostructContext voCtx:
                    Add(voCtx.V);  // VOSTRUCT
                    AddOnClosingLine(XSharpParser.END, XSharpParser.VOSTRUCT);
                    break;

                // ── UNION / END UNION ─────────────────────────────────────────────
                case XSharpParser.VounionContext voUnCtx:
                    Add(voUnCtx.U);  // UNION
                    AddOnClosingLine(XSharpParser.END, XSharpParser.UNION);
                    break;

                // ── WITH / ENDWITH ────────────────────────────────────────────────
                case XSharpParser.WithBlockContext withCtx:
                    Add(withCtx.Start);  // WITH
                    // Closer: ENDWITH (single) or END WITH (two tokens)
                    if (withCtx.e?.Type == XSharpParser.END)
                        AddOnClosingLine(XSharpParser.END, XSharpParser.WITH);
                    else
                        Add(withCtx.e);  // ENDWITH
                    break;
            }
        }
    }
}
