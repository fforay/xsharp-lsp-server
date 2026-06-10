using LanguageService.CodeAnalysis.Text;
using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using LanguageService.SyntaxTree.Tree;
using XSharp.Parser;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Models;
using XSharpLanguageServer.Services;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles <c>textDocument/codeAction</c>.
    /// <para>
    /// Offers the following actions:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Fix all keyword casing</b> (<c>source.fixAll</c>) — scans every
    ///     keyword token in the open document and emits one
    ///     <see cref="TextEdit"/> per token whose text does not match the
    ///     configured <c>KeywordCase</c> setting.
    ///   </item>
    ///   <item>
    ///     <b>Add USING namespace</b> (<c>quickfix</c>) — looks up the word
    ///     under the cursor in <c>ReferencedTypes</c> in the assembly DB,
    ///     falling back to source types declared in a <c>BEGIN NAMESPACE</c>
    ///     block elsewhere in the workspace index. If a namespace is found,
    ///     inserts a <c>USING</c> directive after the last existing
    ///     <c>USING</c> line (or at the top of the file). Not offered when
    ///     the namespace is already imported.
    ///   </item>
    ///   <item>
    ///     <b>Introduce variable</b> (<c>refactor.extract</c>) — wraps the
    ///     selected single-line expression in a <c>LOCAL newVar := expr</c>
    ///     declaration.
    ///   </item>
    ///   <item>
    ///     <b>Extract to function / method</b> (<c>refactor.extract</c>) —
    ///     moves the selected multi-line block into a new
    ///     <c>FUNCTION</c> or <c>METHOD</c>, with parameters inferred from
    ///     the locals and parameters used in the selection.
    ///   </item>
    ///   <item>
    ///     <b>Inline variable</b> (<c>refactor.inline</c>) — replaces all
    ///     usages of a <c>LOCAL foo := expr</c> variable within its scope
    ///     with the initializer expression and removes the declaration.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    public class XSharpCodeActionHandler : CodeActionHandlerBase
    {
        private readonly XSharpDocumentService      _documentService;
        private readonly XSharpDatabaseService      _dbService;
        private readonly XSharpWorkspaceIndex       _workspaceIndex;
        private readonly XSharpConfigurationService _configService;
        private readonly ILogger<XSharpCodeActionHandler> _logger;

        // Matches a USING statement line (case-insensitive).
        private static readonly Regex _usingPattern =
            new(@"^\s*USING\s+[\w.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches a single-variable LOCAL initializer: LOCAL foo := expr
        private static readonly Regex _localInitPattern =
            new(@"^(\s*)LOCAL\s+(\w+)\s*:=\s*(.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public XSharpCodeActionHandler(
            XSharpDocumentService       documentService,
            XSharpDatabaseService       dbService,
            XSharpWorkspaceIndex        workspaceIndex,
            XSharpConfigurationService  configService,
            ILogger<XSharpCodeActionHandler> logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
            _workspaceIndex  = workspaceIndex;
            _configService   = configService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override CodeActionRegistrationOptions CreateRegistrationOptions(
            CodeActionCapability capability,
            ClientCapabilities clientCapabilities)
            => new CodeActionRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                CodeActionKinds  = new Container<CodeActionKind>(
                    CodeActionKind.SourceFixAll,
                    CodeActionKind.QuickFix,
                    CodeActionKind.RefactorExtract,
                    CodeActionKind.RefactorInline),
                ResolveProvider  = false,
            };

        /// <inheritdoc/>
        public override Task<CommandOrCodeActionContainer?> Handle(
            CodeActionParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                var uri   = request.TextDocument.Uri;
                var items = new List<CommandOrCodeAction>();

                // ── Fix all keyword casing ────────────────────────────────
                var edits = ComputeCasingEdits(uri, cancellationToken);
                if (edits.Count > 0)
                {
                    var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                    {
                        [uri] = edits,
                    };

                    items.Add(new CommandOrCodeAction(new CodeAction
                    {
                        Title       = "Fix all keyword casing",
                        Kind        = CodeActionKind.SourceFixAll,
                        IsPreferred = false,
                        Edit        = new WorkspaceEdit { Changes = changes },
                    }));
                }

                // ── Add USING namespace / Refactoring actions ────────────
                if (_documentService.TryGetText(uri, out var text))
                {
                    string? filePath = uri.GetFileSystemPath();

                    // Add USING (quick fix)
                    string word = ExtractWord(text, request.Range.Start);
                    if (!string.IsNullOrEmpty(word))
                        items.AddRange(ComputeAddUsingActions(uri, text, word));

                    bool hasSelection = request.Range.Start.Line != request.Range.End.Line
                        || request.Range.Start.Character != request.Range.End.Character;

                    // ── Step 23: Introduce variable ───────────────────────
                    if (hasSelection
                        && request.Range.Start.Line == request.Range.End.Line)
                    {
                        var action = ComputeIntroduceVariableAction(uri, text, request.Range);
                        if (action != null) items.Add(new CommandOrCodeAction(action));
                    }

                    // ── Step 22: Extract to function / method ────────────
                    if (hasSelection
                        && request.Range.Start.Line != request.Range.End.Line)
                    {
                        var fnAction = ComputeExtractFunctionAction(uri, text, request.Range);
                        if (fnAction != null) items.Add(new CommandOrCodeAction(fnAction));

                        var mAction = ComputeExtractMethodAction(uri, text, request.Range);
                        if (mAction != null) items.Add(new CommandOrCodeAction(mAction));
                    }

                    // ── Step 24: Inline variable ──────────────────────────
                    if (filePath != null)
                    {
                        var action = ComputeInlineVariableAction(
                            uri, text, filePath, request.Range.Start);
                        if (action != null) items.Add(new CommandOrCodeAction(action));
                    }

                    // ── P3-2: Implement interface ─────────────────────────
                    items.AddRange(ComputeImplementInterfaceActions(uri, text, request.Range.Start));
                }

                _logger.LogInformation(
                    "CodeAction: {Count} action(s) for {Uri}", items.Count, uri);

                return Task.FromResult<CommandOrCodeActionContainer?>(
                    items.Count > 0 ? new CommandOrCodeActionContainer(items) : null);
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult<CommandOrCodeActionContainer?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CodeAction failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<CommandOrCodeActionContainer?>(null);
            }
        }

        /// <inheritdoc/>
        public override Task<CodeAction> Handle(
            CodeAction request,
            CancellationToken cancellationToken)
            => Task.FromResult(request);

        // ====================================================================
        // Step 23 — Introduce variable
        // ====================================================================

        private static CodeAction? ComputeIntroduceVariableAction(
            DocumentUri uri, string text, LspRange selection)
        {
            var lines    = text.Split('\n');
            int line     = (int)selection.Start.Line;
            int startCol = (int)selection.Start.Character;
            int endCol   = (int)selection.End.Character;

            if (line >= lines.Length) return null;

            string rawLine = lines[line].TrimEnd('\r');
            if (endCol > rawLine.Length) return null;

            string expr   = rawLine.Substring(startCol, endCol - startCol).Trim();
            if (string.IsNullOrEmpty(expr)) return null;

            string indent  = rawLine[..(rawLine.Length - rawLine.TrimStart().Length)];
            string eol     = text.Contains('\r') ? "\r\n" : "\n";
            string varName = "newVar";

            // Insert LOCAL before the current line, then replace selection with varName.
            // Edits are non-overlapping (insert at col 0, replace at startCol–endCol)
            // and are applied right-to-left by LSP clients, so positions are stable.
            var edits = new List<TextEdit>
            {
                new TextEdit
                {
                    Range   = new LspRange(new Position(line, 0), new Position(line, 0)),
                    NewText = $"{indent}LOCAL {varName} := {expr}{eol}",
                },
                new TextEdit
                {
                    Range   = new LspRange(
                                  new Position(line, startCol),
                                  new Position(line, endCol)),
                    NewText = varName,
                },
            };

            return new CodeAction
            {
                Title       = "Introduce variable",
                Kind        = CodeActionKind.RefactorExtract,
                IsPreferred = true,
                Edit        = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = edits },
                },
            };
        }

        // ====================================================================
        // Step 22 — Extract to function (with parameter analysis)
        // ====================================================================

        private CodeAction? ComputeExtractFunctionAction(
            DocumentUri uri, string text, LspRange selection)
        {
            var lines  = text.Split('\n');
            int startL = (int)selection.Start.Line;
            int endL   = (int)selection.End.Line;

            if (startL >= lines.Length || endL >= lines.Length) return null;

            string eol        = text.Contains('\r') ? "\r\n" : "\n";
            string bodyIndent = "    ";
            string funcName   = "NewFunction";

            // ── Parameter analysis ────────────────────────────────────────
            var parameters = new List<(string Name, string TypeName)>();

            if (_documentService.TryGetParsed(uri, out var parsed)
                && parsed.Tree != null
                && parsed.TokenStream is BufferedTokenStream stream)
            {
                stream.Fill();

                // 1. All unique identifier names used inside the selection.
                var idsInSelection = CollectIdTokensInRange(stream, startL, endL);

                // 2. Enclosing function context + its signature.
                FindEnclosingFunc(parsed.Tree, selection.Start,
                    out var funcCtx, out var sig);

                // 3. LOCALs/VARs declared INSIDE the selection — not parameters.
                var localsInside = CollectLocalsInRange(funcCtx, startL, endL);

                // 4. LOCALs/VARs declared BEFORE the selection in this function.
                var localsBefore = CollectLocalsBefore(funcCtx, startL);

                // 5. Parameters of the enclosing function.
                var funcParamMap = CollectFuncParams(sig);

                // 6. Classify each identifier.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in idsInSelection)
                {
                    if (!seen.Add(id)) continue;
                    if (IsXSharpKeyword(id)) continue;
                    if (localsInside.ContainsKey(id)) continue;         // declared inside → not a param
                    if (IsGlobalSymbol(id)) continue;                   // global func/class/define → skip

                    string? typeName = null;
                    if (localsBefore.TryGetValue(id, out var lt)) typeName = lt;
                    else if (funcParamMap.TryGetValue(id, out var pt)) typeName = pt;
                    else continue;                                       // unknown — could be SELF field, skip

                    parameters.Add((id, CleanParamType(typeName)));
                }
            }

            // ── Build the function text ───────────────────────────────────
            string paramDecl = parameters.Count > 0
                ? string.Join(", ", parameters.Select(p => $"{p.Name} AS {p.TypeName}"))
                : string.Empty;

            string callArgs = parameters.Count > 0
                ? string.Join(", ", parameters.Select(p => p.Name))
                : string.Empty;

            var bodyLines = new List<string>();
            for (int i = startL; i <= endL; i++)
                bodyLines.Add(bodyIndent + lines[i].TrimEnd('\r').TrimStart());

            string body = string.Join(eol, bodyLines);

            string lastLine = lines[^1].TrimEnd('\r');
            int    lastCol  = lastLine.Length;

            string newFunc = $"{eol}{eol}FUNCTION {funcName}({paramDecl}){eol}" +
                             $"{body}{eol}" +
                             $"RETURN{eol}";

            string callIndent = lines[startL].TrimEnd('\r')[
                ..(lines[startL].Length - lines[startL].TrimStart().Length)];

            var edits = new List<TextEdit>
            {
                new TextEdit
                {
                    Range   = new LspRange(
                                  new Position(lines.Length - 1, lastCol),
                                  new Position(lines.Length - 1, lastCol)),
                    NewText = newFunc,
                },
                new TextEdit
                {
                    Range   = new LspRange(
                                  new Position(startL, 0),
                                  new Position(endL, lines[endL].TrimEnd('\r').Length)),
                    NewText = $"{callIndent}{funcName}({callArgs})",
                },
            };

            return new CodeAction
            {
                Title = "Extract to function",
                Kind  = CodeActionKind.RefactorExtract,
                Edit  = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = edits },
                },
            };
        }

        // ====================================================================
        // Extract to method (inside the enclosing class)
        // ====================================================================

        private CodeAction? ComputeExtractMethodAction(
            DocumentUri uri, string text, LspRange selection)
        {
            var lines  = text.Split('\n');
            int startL = (int)selection.Start.Line;
            int endL   = (int)selection.End.Line;

            if (startL >= lines.Length || endL >= lines.Length) return null;

            if (!_documentService.TryGetParsed(uri, out var parsed)
                || parsed.Tree == null
                || parsed.TokenStream is not BufferedTokenStream stream)
                return null;

            stream.Fill();

            // Must be inside a class method (not a standalone FUNCTION/PROCEDURE).
            FindEnclosingFunc(parsed.Tree, selection.Start, out var funcCtx, out var sig);
            if (funcCtx is not XSharpParser.MethodContext methodCtx) return null;

            // Find the enclosing class so we know where to insert the new method.
            FindEnclosingClass(parsed.Tree, selection.Start, out var classCtx);
            if (classCtx?.Stop == null) return null;

            // Is the enclosing method STATIC?
            bool isStatic = methodCtx.Mods != null &&
                Regex.IsMatch(methodCtx.Mods.SourceText ?? "", @"\bSTATIC\b",
                    RegexOptions.IgnoreCase);

            // ── Parameter analysis (same logic as extract-to-function) ────
            var parameters = new List<(string Name, string TypeName)>();
            var idsInSelection = CollectIdTokensInRange(stream, startL, endL);
            var localsInside   = CollectLocalsInRange(funcCtx, startL, endL);
            var localsBefore   = CollectLocalsBefore(funcCtx, startL);
            var funcParamMap   = CollectFuncParams(sig);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in idsInSelection)
            {
                if (!seen.Add(id)) continue;
                if (IsXSharpKeyword(id)) continue;
                if (localsInside.ContainsKey(id)) continue;
                if (IsGlobalSymbol(id)) continue;

                string? typeName = null;
                if (localsBefore.TryGetValue(id, out var lt)) typeName = lt;
                else if (funcParamMap.TryGetValue(id, out var pt)) typeName = pt;
                else continue;

                parameters.Add((id, CleanParamType(typeName)));
            }

            // ── Formatting ───────────────────────────────────────────────
            string eol        = text.Contains('\r') ? "\r\n" : "\n";
            string methodName = "NewMethod";
            string staticKw   = isStatic ? "STATIC " : "";

            // Detect the indentation of the enclosing METHOD line and derive body indent.
            int    encMethodLine  = Math.Max(0, methodCtx.Start.Line - 1);
            string encLineText    = encMethodLine < lines.Length ? lines[encMethodLine] : "";
            string methIndent     = encLineText.Substring(
                0, encLineText.Length - encLineText.TrimStart().Length);
            string bodyIndent     = methIndent + "    ";

            string paramDecl = parameters.Count > 0
                ? string.Join(", ", parameters.Select(p => $"{p.Name} AS {p.TypeName}"))
                : string.Empty;
            string callArgs = parameters.Count > 0
                ? string.Join(", ", parameters.Select(p => p.Name))
                : string.Empty;

            var bodyLines = new List<string>();
            for (int i = startL; i <= endL; i++)
                bodyLines.Add(bodyIndent + lines[i].TrimEnd('\r').TrimStart());
            string body = string.Join(eol, bodyLines);

            // New method inserted just before the ENDCLASS token.
            int endClassLine = Math.Max(0, classCtx.Stop.Line - 1);

            string newMethod =
                $"{eol}{methIndent}{staticKw}METHOD {methodName}({paramDecl}) AS VOID{eol}" +
                $"{body}{eol}" +
                $"{methIndent}RETURN{eol}";

            // Call site: SELF:Method() for instance, bare name for static.
            string callIndent = lines[startL].TrimEnd('\r')[
                ..(lines[startL].Length - lines[startL].TrimStart().Length)];
            string callPrefix = isStatic ? "" : "SELF:";

            var edits = new List<TextEdit>
            {
                // Insert the new method before ENDCLASS.
                new TextEdit
                {
                    Range   = new LspRange(
                                  new Position(endClassLine, 0),
                                  new Position(endClassLine, 0)),
                    NewText = newMethod,
                },
                // Replace selected lines with the method call.
                new TextEdit
                {
                    Range   = new LspRange(
                                  new Position(startL, 0),
                                  new Position(endL, lines[endL].TrimEnd('\r').Length)),
                    NewText = $"{callIndent}{callPrefix}{methodName}({callArgs})",
                },
            };

            string title = isStatic ? "Extract to static method" : "Extract to method";
            return new CodeAction
            {
                Title = title,
                Kind  = CodeActionKind.RefactorExtract,
                Edit  = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = edits },
                },
            };
        }

        // ── P3-2: Implement interface ─────────────────────────────────────────

        /// <summary>
        /// Offers one code action per interface in the enclosing class's IMPLEMENTS
        /// clause that has members missing from the class body.  Inserts stub
        /// implementations just before the END CLASS token.
        /// </summary>
        private IEnumerable<CommandOrCodeAction> ComputeImplementInterfaceActions(
            DocumentUri uri, string text, Position cursor)
        {
            if (!_documentService.TryGetParsed(uri, out var parsed) || parsed.Tree == null)
                yield break;

            FindEnclosingClass(parsed.Tree, cursor, out var classCtx);
            if (classCtx?.Id == null || classCtx.Stop == null
                || classCtx._Implements == null || classCtx._Implements.Count == 0)
                yield break;

            string className = classCtx.Id.GetText();

            // Collect the names already implemented by the class (name-only, case-insensitive).
            var classMemberNames = new HashSet<string>(
                _workspaceIndex.GetMembersOf(className).Select(m => m.Name),
                StringComparer.OrdinalIgnoreCase);

            // Detect indent of the class body from the source (first non-blank member line).
            var lines = text.Split('\n');
            int classStart = Math.Max(0, classCtx.Start.Line - 1);
            int classStop  = Math.Max(0, classCtx.Stop.Line  - 1);
            string memberIndent = DetectMemberIndent(lines, classStart, classStop);

            string eol = text.Contains('\r') ? "\r\n" : "\n";
            int insertLine = classStop;   // insert before END CLASS

            foreach (var ifaceNode in classCtx._Implements)
            {
                string ifaceName = ifaceNode.GetText();
                if (string.IsNullOrEmpty(ifaceName)) continue;

                // Tier-1: workspace index (source-defined interfaces)
                IReadOnlyList<Models.WorkspaceSymbol> ifaceMembers = _workspaceIndex.GetMembersOf(ifaceName);

                // Tier-2: assembly reflection fallback
                if (ifaceMembers.Count == 0 && _dbService.IsAvailable)
                    ifaceMembers = _dbService.FindAssemblyMembersOf(ifaceName);

                if (ifaceMembers.Count == 0) continue;

                var missing = ifaceMembers
                    .Where(m => m.Kind != XSharpSymbolKind.Constructor
                             && m.Kind != XSharpSymbolKind.Destructor
                             && !classMemberNames.Contains(m.Name))
                    .ToList();

                if (missing.Count == 0) continue;

                var sb = new StringBuilder();
                foreach (var m in missing)
                    sb.Append(GenerateMemberStub(m, memberIndent, eol));

                var edit = new TextEdit
                {
                    NewText = sb.ToString(),
                    Range   = new LspRange(new Position(insertLine, 0), new Position(insertLine, 0)),
                };

                yield return new CommandOrCodeAction(new CodeAction
                {
                    Title = $"Implement interface '{ifaceName}' ({missing.Count} missing member(s))",
                    Kind  = CodeActionKind.QuickFix,
                    Edit  = new WorkspaceEdit
                    {
                        Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                        {
                            [uri] = new[] { edit },
                        },
                    },
                });
            }
        }

        /// <summary>
        /// Detects the indentation string used for class body members by scanning
        /// for the first non-blank line inside the class range that is indented
        /// more than the class declaration line itself.  Falls back to 3 spaces.
        /// </summary>
        private static string DetectMemberIndent(string[] lines, int classStart, int classStop)
        {
            string classDeclTrimmed = classStart < lines.Length
                ? lines[classStart].TrimStart() : string.Empty;
            int classIndentLen = classStart < lines.Length
                ? lines[classStart].Length - classDeclTrimmed.Length : 0;

            for (int i = classStart + 1; i < classStop && i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                int indentLen = line.Length - line.TrimStart().Length;
                if (indentLen > classIndentLen)
                    return line.Substring(0, indentLen);
            }

            return new string(' ', classIndentLen + 3);
        }

        /// <summary>
        /// Generates an XSharp stub implementation for a single interface member.
        /// </summary>
        private static string GenerateMemberStub(Models.WorkspaceSymbol member, string indent, string eol)
        {
            string src = member.Sourcecode?.Trim() ?? string.Empty;
            string bodyIndent = indent + "   ";

            bool isProperty = member.Kind == XSharpSymbolKind.Property
                || src.StartsWith("PROPERTY", StringComparison.OrdinalIgnoreCase);
            bool isEvent = member.Kind == XSharpSymbolKind.Event
                || src.StartsWith("EVENT", StringComparison.OrdinalIgnoreCase);

            if (isEvent)
            {
                // Events are field-like declarations — just emit the declaration.
                string decl = string.IsNullOrEmpty(src)
                    ? $"EVENT {member.Name} AS EventHandler"
                    : src;
                return $"{indent}{decl}{eol}{eol}";
            }

            if (isProperty)
            {
                string retType = member.ReturnType ?? "OBJECT";
                return
                    $"{indent}PROPERTY {member.Name} AS {retType}{eol}" +
                    $"{bodyIndent}GET{eol}" +
                    $"{bodyIndent}   THROW NotImplementedException{{}}{eol}" +
                    $"{bodyIndent}END GET{eol}" +
                    $"{bodyIndent}SET{eol}" +
                    $"{bodyIndent}   THROW NotImplementedException{{}}{eol}" +
                    $"{bodyIndent}END SET{eol}" +
                    $"{indent}END PROPERTY{eol}{eol}";
            }

            // Method, ACCESS, ASSIGN, or unknown — use the prototype as-is.
            if (string.IsNullOrEmpty(src))
                src = $"METHOD {member.Name}()";

            return
                $"{indent}{src}{eol}" +
                $"{bodyIndent}THROW NotImplementedException{{}}{eol}{eol}";
        }

        // Finds the innermost Class_Context whose range contains the cursor.
        private static void FindEnclosingClass(
            IParseTree root, Position cursor,
            out XSharpParser.Class_Context? classCtx)
        {
            classCtx = null;
            WalkForClass(root, cursor, ref classCtx);
        }

        private static void WalkForClass(
            IParseTree node, Position cursor,
            ref XSharpParser.Class_Context? classCtx)
        {
            if (node is not XSharpParserRuleContext ctx) return;

            int startLine = ctx.Start != null ? Math.Max(0, ctx.Start.Line - 1) : 0;
            int stopLine  = ctx.Stop  != null ? Math.Max(0, ctx.Stop.Line  - 1) : startLine;
            if (cursor.Line < startLine || cursor.Line > stopLine) return;

            if (ctx is XSharpParser.Class_Context cc)
                classCtx = cc;  // keep updating so we end up with the innermost match

            for (int i = 0; i < node.ChildCount; i++)
                WalkForClass(node.GetChild(i), cursor, ref classCtx);
        }

        // ── Parameter analysis helpers ─────────────────────────────────────

        private const int TokenTypeId = 351;

        private static HashSet<string> CollectIdTokensInRange(
            BufferedTokenStream stream, int startLine, int endLine)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokens = stream.GetTokens();
            if (tokens == null) return result;

            foreach (var tok in tokens)
            {
                if (tok.Type != TokenTypeId) continue;
                if (string.IsNullOrEmpty(tok.Text)) continue;
                int line = Math.Max(0, tok.Line - 1);
                if (line >= startLine && line <= endLine)
                    result.Add(tok.Text);
            }

            return result;
        }

        private static void FindEnclosingFunc(
            IParseTree root, Position cursor,
            out XSharpParserRuleContext? funcCtx,
            out XSharpParser.SignatureContext? sig)
        {
            XSharpScopeHelper.FindEnclosingFunction(
                root, cursor,
                out funcCtx, out sig,
                out _, out _);
        }

        // Collect LOCALs/VARs declared INSIDE [startLine..endLine].
        private static Dictionary<string, string?> CollectLocalsInRange(
            XSharpParserRuleContext? root, int startLine, int endLine)
            => XSharpScopeHelper.CollectLocalsInRange(root, startLine, endLine);

        // Collect LOCALs/VARs declared BEFORE startLine in the enclosing function.
        private static Dictionary<string, string?> CollectLocalsBefore(
            XSharpParserRuleContext? root, int startLine)
            => XSharpScopeHelper.CollectLocalsInRange(root, 0, startLine - 1);

        private static Dictionary<string, string?> CollectFuncParams(
            XSharpParser.SignatureContext? sig)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (sig?.ParamList?._Params == null) return result;
            foreach (var p in sig.ParamList._Params)
                if (p.Id != null)
                    result.TryAdd(p.Id.GetText(), p.Type?.GetText());
            return result;
        }

        private bool IsGlobalSymbol(string name)
        {
            var sym = _workspaceIndex.FindExact(name);
            if (sym == null || sym.TypeName != null) return false;  // member, not global
            return sym.Kind == XSharpSymbolKind.Function  ||
                   sym.Kind == XSharpSymbolKind.Procedure ||
                   sym.Kind == XSharpSymbolKind.Class     ||
                   sym.Kind == XSharpSymbolKind.Interface ||
                   sym.Kind == XSharpSymbolKind.Structure ||
                   sym.Kind == XSharpSymbolKind.Enum      ||
                   sym.Kind == XSharpSymbolKind.Delegate  ||
                   sym.Kind == XSharpSymbolKind.Define    ||
                   sym.Kind == XSharpSymbolKind.Global    ||
                   sym.Kind == XSharpSymbolKind.Namespace;
        }

        private static bool IsXSharpKeyword(string name)
            => XSharpFormattingHandler.KeywordMap.Values
                   .Contains(name.ToUpperInvariant(), StringComparer.Ordinal);

        private static string CleanParamType(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "USUAL";
            int lt  = raw.IndexOf('<'); if (lt > 0) raw = raw[..lt];
            raw     = raw.TrimEnd('?', '[', ']', ' ').Trim();
            int dot = raw.LastIndexOf('.'); if (dot >= 0) raw = raw[(dot + 1)..];
            return string.IsNullOrEmpty(raw) ? "USUAL" : raw;
        }

        // ====================================================================
        // Step 24 — Inline variable
        // ====================================================================

        private CodeAction? ComputeInlineVariableAction(
            DocumentUri uri, string text, string filePath, Position cursor)
        {
            var lines   = text.Split('\n');
            int lineIdx = (int)cursor.Line;
            if (lineIdx >= lines.Length) return null;

            string rawLine = lines[lineIdx].TrimEnd('\r');

            // Only activate on a LOCAL foo := expr declaration.
            var m = _localInitPattern.Match(rawLine);
            if (!m.Success) return null;

            string varName = m.Groups[2].Value;
            string initExpr = m.Groups[3].Value.Trim();
            if (string.IsNullOrEmpty(varName) || string.IsNullOrEmpty(initExpr)) return null;

            // Determine function scope: [funcStartLine, funcEndLine].
            var fileSymbols  = _workspaceIndex.GetSymbolsInFile(filePath);
            int funcStart    = 0;
            int funcEnd      = lines.Length - 1;

            for (int i = 0; i < fileSymbols.Count; i++)
            {
                var sym = fileSymbols[i];
                if (!IsCallable(sym.Kind)) continue;
                if (sym.StartLine > lineIdx) break;
                funcStart = sym.StartLine;
                funcEnd   = i + 1 < fileSymbols.Count ? fileSymbols[i + 1].StartLine - 1 : lines.Length - 1;
            }

            // Collect all token locations of varName within the function scope.
            var allLocs = _workspaceIndex.FindTokenLocations(varName)
                .Where(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
                         && t.Line >= funcStart
                         && t.Line <= funcEnd
                         && t.Line != lineIdx)    // skip the declaration itself
                .ToList();

            if (allLocs.Count == 0) return null;

            string eol  = text.Contains('\r') ? "\r\n" : "\n";
            var edits   = new List<TextEdit>();

            // Delete the LOCAL declaration line (including the line ending).
            string deleteTo = lineIdx + 1 < lines.Length
                ? $""  // will be handled by the range
                : string.Empty;
            edits.Add(new TextEdit
            {
                Range = new LspRange(
                    new Position(lineIdx, 0),
                    new Position(lineIdx + 1, 0)),
                NewText = string.Empty,
            });

            // Replace each usage with the initializer expression.
            // Sort descending so earlier edits don't shift later positions.
            foreach (var tok in allLocs.OrderByDescending(t => t.Line).ThenByDescending(t => t.Col))
            {
                edits.Add(new TextEdit
                {
                    Range = new LspRange(
                        new Position(tok.Line, tok.Col),
                        new Position(tok.Line, tok.Col + tok.Text.Length)),
                    NewText = initExpr,
                });
            }

            return new CodeAction
            {
                Title = $"Inline variable '{varName}'",
                Kind  = CodeActionKind.RefactorInline,
                Edit  = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = edits },
                },
            };
        }

        private static bool IsCallable(int kind) =>
            kind == XSharpSymbolKind.Function  ||
            kind == XSharpSymbolKind.Procedure ||
            kind == XSharpSymbolKind.Method    ||
            kind == XSharpSymbolKind.Constructor;

        // ====================================================================
        // Add USING namespace
        // ====================================================================

        private List<CommandOrCodeAction> ComputeAddUsingActions(
            DocumentUri uri, string text, string typeName)
        {
            var results = new List<CommandOrCodeAction>();

            // Assembly types first (DB lookup), then source types declared in
            // a BEGIN NAMESPACE block elsewhere in the workspace.
            string? ns = _dbService.IsAvailable ? _dbService.FindAssemblyTypeNamespace(typeName) : null;
            if (string.IsNullOrEmpty(ns))
                ns = _workspaceIndex.FindExact(typeName)?.Namespace;
            if (string.IsNullOrEmpty(ns)) return results;

            var lines = text.Split('\n');

            // Skip if USING ns already exists.
            foreach (var line in lines)
            {
                if (_usingPattern.IsMatch(line) &&
                    line.Contains(ns, StringComparison.OrdinalIgnoreCase))
                    return results;
            }

            // Find insertion point: after the last existing USING line,
            // or at line 0 if no USING exists.
            int insertLine = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (_usingPattern.IsMatch(lines[i]))
                    insertLine = i + 1;
            }

            var edit = new TextEdit
            {
                Range   = new LspRange(new Position(insertLine, 0), new Position(insertLine, 0)),
                NewText = $"USING {ns}{(text.Contains('\r') ? "\r\n" : "\n")}",
            };

            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = new[] { edit } };

            results.Add(new CommandOrCodeAction(new CodeAction
            {
                Title       = $"Add USING {ns}",
                Kind        = CodeActionKind.QuickFix,
                IsPreferred = true,
                Edit        = new WorkspaceEdit { Changes = changes },
            }));

            return results;
        }

        private static string ExtractWord(string text, Position pos)
        {
            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return string.Empty;
            string line = lines[(int)pos.Line].TrimEnd('\r');
            int col     = Math.Min((int)pos.Character, line.Length);
            int start   = col;
            while (start > 0 && IsIdentChar(line[start - 1])) start--;
            int end = col;
            while (end < line.Length && IsIdentChar(line[end])) end++;
            return line.Substring(start, end - start);
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private sealed class NullErrorListener : VsParser.IErrorListener
        {
            public void ReportError(string f, LinePositionSpan s, string c, string m, object[] a) { }
            public void ReportWarning(string f, LinePositionSpan s, string c, string m, object[] a) { }
        }

        // ====================================================================
        // Keyword casing
        // ====================================================================

        /// <summary>
        /// Returns one <see cref="TextEdit"/> per keyword token that does not
        /// match its canonical UPPER-CASE spelling.
        /// </summary>
        private List<TextEdit> ComputeCasingEdits(
            DocumentUri uri,
            CancellationToken cancellationToken)
        {
            var edits = new List<TextEdit>();

            if (!_documentService.TryGetText(uri, out var text)) return edits;

            // Re-lex with stddefs disabled so the preprocessor does not replace
            // UDC tokens (DO FORM, READ EVENTS, …) with UDC_KEYWORD type tokens —
            // the same fix applied in XSharpFormattingHandler.
            var formattingOptions = _configService.GetFormattingParseOptions();
            string filePath = uri.GetFileSystemPath() ?? uri.ToString();
            VsParser.Lex(text, filePath, formattingOptions,
                         new NullErrorListener(),
                         out var lexedStream, out _);

            if (lexedStream is not BufferedTokenStream stream) return edits;

            stream.Fill();
            var tokens = stream.GetTokens();
            if (tokens == null) return edits;

            var keywordMap = XSharpFormattingHandler.KeywordMap;
            string kwCase  = _configService.GetSettings().KeywordCase;

            foreach (var token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (token.Channel != 0) continue;           // hidden channel
                if (token.Type == -1)   continue;           // EOF
                if (XSharpLexer.IsString(token.Type))  continue;
                if (XSharpLexer.IsComment(token.Type)) continue;

                if (!keywordMap.TryGetValue(token.Type, out var upper)) continue;
                var canonical = XSharpFormattingHandler.ApplyKeywordCase(upper, kwCase);
                if (canonical == null) continue;  // "None" — skip
                if (string.Equals(token.Text, canonical, StringComparison.Ordinal)) continue;

                // Wrong casing — emit a minimal TextEdit.
                int line   = Math.Max(0, token.Line - 1);   // 1-based → 0-based
                int col    = Math.Max(0, token.Column);
                int endCol = col + token.Text.Length;

                edits.Add(new TextEdit
                {
                    Range   = new LspRange(new Position(line, col), new Position(line, endCol)),
                    NewText = canonical,
                });
            }

            return edits;
        }
    }
}
