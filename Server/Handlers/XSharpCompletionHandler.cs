using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using LanguageService.SyntaxTree.Misc;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

using XSharpLanguageServer.Services;
using XSharpLanguageServer.Models;
namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>textDocument/completion</c> LSP request.
    /// <para>
    /// Provides three categories of completion items, merged into a single
    /// deduplicated list via a shared <see cref="HashSet{T}"/>:
    /// <list type="number">
    ///   <item>
    ///     <b>Keywords</b> — all XSharp keywords and type keywords, built once at startup
    ///     from <see cref="XSharpLexer.DefaultVocabulary"/> and filtered by the prefix
    ///     the user has already typed.  Items are offered in uppercase (the XSharp
    ///     convention) but the filter is case-insensitive.
    ///   </item>
    ///   <item>
    ///     <b>Document symbols</b> — identifiers declared in the current file
    ///     (classes, methods, functions, fields, …) extracted by walking the parse tree.
    ///   </item>
    ///   <item>
    ///     <b>DB cross-file symbols</b> — types and global members from the XSharp
    ///     IntelliSense database, filtered by prefix or by member type on <c>.</c>/<c>:</c>.
    ///   </item>
    /// </list>
    /// A single <see cref="HashSet{T}"/> (case-insensitive) is shared across all three
    /// passes so a name that appears in more than one source is only emitted once.
    /// </para>
    /// </summary>
    public class XSharpCompletionHandler : CompletionHandlerBase
    {
        private readonly XSharpDocumentService _documentService;
        private readonly XSharpDatabaseService _dbService;
        private readonly XSharpWorkspaceIndex _workspaceIndex;
        private readonly ILogger<XSharpCompletionHandler> _logger;

        /// <summary>
        /// Keyword completion items built once from the lexer vocabulary.
        /// Immutable after construction — safe to share across all requests.
        /// </summary>
        private static readonly ImmutableArray<CompletionItem> _keywordItems =
            BuildKeywordItems();

        /// <summary>
        /// Snippet completion items — one per LS snippet, built once at startup.
        /// Added before keywords so snippet variants of keywords (IF, FOR, …) win.
        /// </summary>
        private static readonly ImmutableArray<CompletionItem> _snippetItems =
            BuildSnippetItems();

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpCompletionHandler(
            XSharpDocumentService documentService,
            XSharpDatabaseService dbService,
            XSharpWorkspaceIndex workspaceIndex,
            ILogger<XSharpCompletionHandler> logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
            _workspaceIndex  = workspaceIndex;
            _logger          = logger;
        }

        /// <summary>
        /// Registers this handler for the <c>"xsharp"</c> language.
        /// Trigger characters: <c>.</c> and <c>:</c> (member access in XSharp).
        /// </summary>
        protected override CompletionRegistrationOptions CreateRegistrationOptions(
            CompletionCapability capability,
            ClientCapabilities clientCapabilities)
        {
            return new CompletionRegistrationOptions
            {
                DocumentSelector  = TextDocumentSelector.ForLanguage("xsharp"),
                TriggerCharacters = new Container<string>(".", ":"),
                ResolveProvider   = false,
            };
        }

        /// <summary>
        /// Entry point for <c>textDocument/completion</c> requests.
        /// </summary>
        public override Task<CompletionList> Handle(
            CompletionParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                // ----------------------------------------------------------------
                // Determine the prefix the user has already typed.
                // ----------------------------------------------------------------
                string prefix = GetWordPrefix(request.TextDocument.Uri, request.Position);

                _logger.LogInformation(
                    "Completion at {Uri} ({Line},{Char}), prefix='{Prefix}'",
                    request.TextDocument.Uri,
                    request.Position.Line, request.Position.Character,
                    prefix);

                var items = new List<CompletionItem>();

                // Single seen-set shared across all three passes — ensures a name
                // that appears in keywords, in-file symbols, AND the DB is only
                // emitted once (first occurrence wins, in priority order).
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // ----------------------------------------------------------------
                // INHERIT / IMPLEMENTS context — filter to classes / interfaces only.
                // Bypass all keyword, snippet, and general-symbol passes.
                // ----------------------------------------------------------------
                var inheritCtx = DetectInheritContext(request.TextDocument.Uri, request.Position);
                if (inheritCtx != InheritContext.None && prefix.Length >= 1)
                {
                    int targetKind = inheritCtx == InheritContext.Inherit
                        ? XSharpSymbolKind.Class
                        : XSharpSymbolKind.Interface;

                    foreach (var sym in _workspaceIndex.FindByPrefix(prefix))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (sym.Kind != targetKind) continue;
                        if (seen.Add(sym.Name))
                            items.Add(SymbolToCompletionItem(sym));
                    }

                    if (_dbService.IsAvailable)
                    {
                        foreach (var sym in _dbService.FindAssemblyByPrefix(prefix))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (sym.Kind != targetKind) continue;
                            if (seen.Add(sym.Name))
                                items.Add(SymbolToCompletionItem(sym));
                        }
                    }

                    _logger.LogInformation(
                        "Completion ({Ctx}): {Count} item(s) for prefix '{Prefix}'",
                        inheritCtx, items.Count, prefix);

                    return Task.FromResult(new CompletionList(items));
                }

                // ----------------------------------------------------------------
                // 0. Snippets — added before keywords so snippet variants of
                //    keywords (IF, FOR, CLASS, …) take priority in the list.
                //    FilterText drives prefix matching for multi-word labels.
                // ----------------------------------------------------------------
                foreach (var snippet in _snippetItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string filterKey = snippet.FilterText ?? snippet.Label;
                    if (prefix.Length == 0
                        || filterKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (seen.Add(snippet.Label))
                            items.Add(snippet);
                    }
                }

                // ----------------------------------------------------------------
                // 1. Keywords — filter by prefix (case-insensitive).
                // ----------------------------------------------------------------
                foreach (var kw in _keywordItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (prefix.Length == 0
                        || kw.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (seen.Add(kw.Label))
                            items.Add(kw);
                    }
                }

                // ----------------------------------------------------------------
                // 2. Document symbols — identifiers from the current parse tree.
                // ----------------------------------------------------------------
                if (_documentService.TryGetParsed(request.TextDocument.Uri, out var parsed)
                    && parsed.Tree != null)
                {
                    CollectSymbolItems(parsed.Tree, prefix, items, cancellationToken, seen);
                }

                // ----------------------------------------------------------------
                // 3. Cross-file symbols — two-tier: workspace index then assembly DB.
                //    Also check for member-access trigger ('.' or ':') to provide
                //    member completion.
                // ----------------------------------------------------------------
                string? memberTypeName = GetMemberAccessType(
                    request.TextDocument.Uri, request.Position);

                if (memberTypeName != null)
                {
                    // Resolve the raw identifier to an actual type name.
                    if (parsed?.Tree != null)
                    {
                        memberTypeName = XSharpTypeResolver.Resolve(
                            parsed.Tree,
                            request.Position,
                            memberTypeName,
                            _workspaceIndex,
                            _dbService) ?? memberTypeName;
                    }

                    // Member access: foo. or foo: → workspace index first, then assembly reflection
                    foreach (var sym in _workspaceIndex.GetMembersOf(memberTypeName))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (seen.Add(sym.Name))
                            items.Add(SymbolToCompletionItem(sym));
                    }

                    // Assembly fallback: BCL / NuGet types via reflection (no DB required)
                    foreach (var sym in _dbService.FindAssemblyMembersOf(memberTypeName))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (seen.Add(sym.Name))
                            items.Add(SymbolToCompletionItem(sym));
                    }                }
                else if (prefix.Length >= 2)
                {
                    // Prefix lookup — only activate for ≥2 chars to limit noise.
                    // Tier 1: workspace index (source symbols).
                    foreach (var sym in _workspaceIndex.FindByPrefix(prefix))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (seen.Add(sym.Name))
                            items.Add(SymbolToCompletionItem(sym));
                    }

                    // Tier 2: assembly fallback.
                    if (_dbService.IsAvailable)
                    {
                        foreach (var sym in _dbService.FindAssemblyByPrefix(prefix))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (seen.Add(sym.Name))
                                items.Add(SymbolToCompletionItem(sym));
                        }
                    }
                }

                _logger.LogInformation(
                    "Completion: {Count} item(s) for prefix '{Prefix}'",
                    items.Count, prefix);

                return Task.FromResult(new CompletionList(items));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(new CompletionList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Completion failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult(new CompletionList());
            }
        }

        /// <inheritdoc/>
        public override Task<CompletionItem> Handle(
            CompletionItem request,
            CancellationToken cancellationToken)
        {
            // ResolveProvider is false — this should never be called.
            return Task.FromResult(request);
        }

        // ====================================================================
        // Prefix extraction
        // ====================================================================

        /// <summary>
        /// Reads the current document text and scans backwards from the cursor
        /// position to find the word the user is currently typing.
        /// Returns an empty string if no prefix can be determined.
        /// </summary>
        private string GetWordPrefix(DocumentUri uri, Position cursor)
        {
            if (!_documentService.TryGetText(uri, out var text))
                return string.Empty;

            // Convert LSP 0-based position to a flat character offset.
            int offset = GetOffset(text, cursor.Line, cursor.Character);
            if (offset <= 0) return string.Empty;

            // Scan backwards while the character is a valid identifier character.
            int start = offset;
            while (start > 0 && IsWordChar(text[start - 1]))
                start--;

            return text.Substring(start, offset - start);
        }

        /// <summary>
        /// Converts a (line, character) pair (0-based, LSP convention) to a flat
        /// character offset within <paramref name="text"/>.
        /// </summary>
        private static int GetOffset(string text, int line, int character)
        {
            int currentLine = 0;
            int i = 0;

            while (i < text.Length && currentLine < line)
            {
                if (text[i] == '\n') currentLine++;
                i++;
            }

            // Add the column offset, clamped to the line length.
            int end = i + character;
            return Math.Min(end, text.Length);
        }

        /// <summary>
        /// Returns <c>true</c> for characters that can appear inside an XSharp identifier
        /// or keyword: letters, digits, and underscore.
        /// </summary>
        private static bool IsWordChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';

        // ====================================================================
        // Document symbol completion
        // ====================================================================

        /// <summary>
        /// Walks the parse tree and adds one <see cref="CompletionItem"/> per named
        /// declaration whose name starts with <paramref name="prefix"/>.
        /// Avoids duplicates by tracking names already added.
        /// </summary>
        private static void CollectSymbolItems(
            IParseTree node,
            string prefix,
            List<CompletionItem> items,
            CancellationToken ct,
            HashSet<string>? seen = null)
        {
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ct.ThrowIfCancellationRequested();

            if (node is XSharpParserRuleContext ctx)
            {
                var (name, kind) = ExtractNameAndKind(ctx);
                if (name != null
                    && seen.Add(name)
                    && (prefix.Length == 0
                        || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(new CompletionItem
                    {
                        Label            = name,
                        Kind             = kind,
                        Detail           = SymbolKindToDetail(kind),
                        SortText         = "~" + name,   // sorts after keywords
                        InsertText       = name,
                    });
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
                CollectSymbolItems(node.GetChild(i), prefix, items, ct, seen);
        }

        /// <summary>
        /// Extracts the declared name and LSP <see cref="CompletionItemKind"/> from
        /// a recognised parse tree node.  Returns <c>(null, default)</c> for nodes
        /// that are not declarations.
        /// </summary>
        private static (string? name, CompletionItemKind kind) ExtractNameAndKind(
            XSharpParserRuleContext ctx)
        {
            return ctx switch
            {
                XSharpParser.Namespace_Context ns   => (ns.Name?.GetText(),   CompletionItemKind.Module),
                XSharpParser.Class_Context cls       => (cls.Id?.GetText(),    CompletionItemKind.Class),
                XSharpParser.Interface_Context iface => (iface.Id?.GetText(),  CompletionItemKind.Interface),
                XSharpParser.Structure_Context s     => (s.Id?.GetText(),      CompletionItemKind.Struct),
                XSharpParser.Enum_Context en         => (en.Id?.GetText(),     CompletionItemKind.Enum),
                XSharpParser.Delegate_Context del    => (del.Id?.GetText(),    CompletionItemKind.Function),
                XSharpParser.VostructContext vos     => (vos.Id?.GetText(),    CompletionItemKind.Struct),
                XSharpParser.VounionContext vou      => (vou.Id?.GetText(),    CompletionItemKind.Struct),
                XSharpParser.FuncprocContext fp      => (fp.Sig?.Id?.GetText(),CompletionItemKind.Function),
                XSharpParser.MethodContext m         => (m.Sig?.Id?.GetText(), CompletionItemKind.Method),
                XSharpParser.ConstructorContext      => ("Constructor",         CompletionItemKind.Constructor),
                XSharpParser.PropertyContext prop    => (prop.Id?.GetText(),   CompletionItemKind.Property),
                XSharpParser.Event_Context evt       => (evt.Id?.GetText(),    CompletionItemKind.Event),
                XSharpParser.EnummemberContext em    => (em.Id?.GetText(),     CompletionItemKind.EnumMember),
                _                                   => (null, default),
            };
        }

        /// <summary>
        /// Returns a short human-readable label for a <see cref="CompletionItemKind"/>,
        /// shown as the <c>detail</c> line in the completion popup.
        /// </summary>
        private static string SymbolKindToDetail(CompletionItemKind kind) => kind switch
        {
            CompletionItemKind.Class       => "class",
            CompletionItemKind.Interface   => "interface",
            CompletionItemKind.Struct      => "struct",
            CompletionItemKind.Enum        => "enum",
            CompletionItemKind.Function    => "function",
            CompletionItemKind.Method      => "method",
            CompletionItemKind.Constructor => "constructor",
            CompletionItemKind.Property    => "property",
            CompletionItemKind.Event       => "event",
            CompletionItemKind.EnumMember  => "enum member",
            CompletionItemKind.Module      => "namespace",
            _                             => string.Empty,
        };

        // ====================================================================
        // Keyword list — built once at startup
        // ====================================================================

        /// <summary>
        /// Iterates every token type between <c>FIRST_KEYWORD</c> and <c>LAST_KEYWORD</c>
        /// (inclusive) and between <c>FIRST_TYPE</c> and <c>LAST_TYPE</c> (inclusive),
        /// asking the lexer vocabulary for the symbolic name of each type and producing
        /// one <see cref="CompletionItem"/> per keyword.
        /// <para>
        /// Sentinel tokens (<c>FIRST_*</c>, <c>LAST_*</c>, <c>FIRST_POSITIONAL_KEYWORD</c>,
        /// <c>LAST_POSITIONAL_KEYWORD</c>) are excluded because they are internal markers,
        /// not real language keywords.
        /// </para>
        /// </summary>
        private static ImmutableArray<CompletionItem> BuildKeywordItems()
        {
            var vocab = XSharpLexer.DefaultVocabulary;
            var builder = ImmutableArray.CreateBuilder<CompletionItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRange(int first, int last)
            {
                for (int type = first + 1; type < last; type++)
                {
                    string? name = vocab.GetSymbolicName(type);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Skip internal sentinel / positional marker tokens.
                    if (name.StartsWith("FIRST_", StringComparison.Ordinal)
                        || name.StartsWith("LAST_", StringComparison.Ordinal))
                        continue;

                    if (!seen.Add(name)) continue;

                    builder.Add(new CompletionItem
                    {
                        Label      = name,             // XSharp convention: uppercase
                        Kind       = CompletionItemKind.Keyword,
                        Detail     = "keyword",
                        SortText   = name,             // keywords sort before symbols (no ~ prefix)
                        InsertText = name,
                    });
                }
            }

            AddRange(XSharpParser.FIRST_KEYWORD, XSharpParser.LAST_KEYWORD);
            AddRange(XSharpParser.FIRST_TYPE,    XSharpParser.LAST_TYPE);

            return builder.ToImmutable();
        }

        // ====================================================================
        // Snippet list — built once at startup, ported from LS .snippet files
        // ====================================================================

        private static ImmutableArray<CompletionItem> BuildSnippetItems()
        {
            var items = ImmutableArray.CreateBuilder<CompletionItem>();

            void Add(string label, string filterText, string description, string insertText) =>
                items.Add(new CompletionItem
                {
                    Label            = label,
                    Kind             = CompletionItemKind.Snippet,
                    Detail           = description,
                    FilterText       = filterText,
                    InsertText       = insertText,
                    InsertTextFormat = InsertTextFormat.Snippet,
                    SortText         = label,
                });

            // ── Control flow ─────────────────────────────────────────────────
            Add("IF", "IF", "IF statement",
                "IF ${1:true}\n   $0\nEND IF");

            Add("IF ELSE", "IF", "IF ELSE statement",
                "IF ${1:true}\n   $0\nELSE\n\nEND IF");

            Add("IF ELSEIF", "IF", "IF ELSEIF ELSE statement",
                "IF ${1:true}\n   $0\nELSEIF ${2:true}\n\nELSE\n\nEND IF");

            Add("FOR", "FOR", "FOR loop",
                "FOR ${1:i} := ${2:start} TO ${3:finish} STEP ${4:incr}\n   $0\nNEXT");

            Add("FOR DOWNTO", "FOR", "FOR DOWNTO loop",
                "FOR ${1:i} := ${2:start} DOWNTO ${3:finish} STEP ${4:incr}\n   $0\nNEXT");

            Add("FOR UPTO", "FOR", "FOR UPTO loop",
                "FOR ${1:i} := ${2:start} UPTO ${3:finish} STEP ${4:incr}\n   $0\nNEXT");

            Add("FOREACH", "FOREACH", "FOREACH loop",
                "FOREACH VAR ${1:item} IN ${2:Collection}\n   $0\nNEXT");

            Add("DO WHILE", "DO", "DO WHILE loop",
                "DO WHILE ${1:true}\n   $0\nENDDO");

            Add("REPEAT", "REPEAT", "REPEAT UNTIL loop",
                "REPEAT\n   $0\nUNTIL ${1:true}");

            Add("DO CASE", "DO", "DO CASE statement",
                "DO CASE\n   CASE $0\n\n   CASE\n\n   OTHERWISE\n\nEND CASE");

            Add("SWITCH", "SWITCH", "SWITCH statement",
                "SWITCH ${1:case_var}\n   CASE ${2:Value1}\n      $0\n   CASE ${3:Value2}\n   OTHERWISE\nEND SWITCH");

            // ── Exception handling ────────────────────────────────────────────
            Add("TRY CATCH", "TRY", "TRY CATCH block",
                "TRY\n   $0\nCATCH ${1:e} AS ${2:System.Exception}\n\nEND TRY");

            Add("TRY CATCH FINALLY", "TRYCF", "TRY CATCH FINALLY block",
                "TRY\n   $0\nCATCH ${1:e} AS ${2:System.Exception}\n\nFINALLY\n\nEND TRY");

            Add("TRY FINALLY", "TRYF", "TRY FINALLY block",
                "TRY\n   $0\nFINALLY\n\nEND TRY");

            Add("BEGIN SEQUENCE", "BEGIN", "BEGIN SEQUENCE block",
                "BEGIN SEQUENCE\n   $0\nRECOVER USING ${1:oError}\n\nEND SEQUENCE");

            // ── Type declarations ─────────────────────────────────────────────
            Add("CLASS", "CLASS", "CLASS declaration",
                "CLASS ${1:MyClass}\n   $0\n\n   CONSTRUCTOR()\n      RETURN\n\nEND CLASS");

            Add("CLASS INHERIT", "CLASS", "CLASS INHERIT declaration",
                "CLASS ${1:MyClass} INHERIT ${2:ParentClass}\n   $0\n\n   CONSTRUCTOR()\n      SUPER()\n      RETURN\n\nEND CLASS");

            Add("INTERFACE", "INTERFACE", "INTERFACE declaration",
                "INTERFACE ${1:IMyInterface}\n   $0\n\nEND INTERFACE");

            Add("STRUCTURE", "STRUCTURE", "STRUCTURE declaration",
                "STRUCTURE ${1:MyStruct}\n   $0\n\n   METHOD DoSomething() AS VOID\n      RETURN\n\nEND STRUCTURE");

            Add("VOSTRUCT", "VOSTRUCT", "VO STRUCTURE declaration",
                "VOSTRUCT ${1:MyStructure}\n   MEMBER ${2:M1} AS ${3:INT}\n   $0");

            Add("PROPERTY", "PROPERTY", "PROPERTY declaration",
                "PROPERTY ${1:MyProperty} AS ${2:OBJECT}\n   GET\n      RETURN $0\n   END GET\n   SET\n      // Use Value for the new value\n      RETURN\n   END SET\nEND PROPERTY");

            // ── Preprocessor ──────────────────────────────────────────────────
            Add("#region", "#region", "#region block",
                "#region ${1:Name}\n   $0\n#endregion");

            Add("#ifdef", "#ifdef", "#ifdef block",
                "#ifdef ${1:Name}\n   $0\n#endif");

            Add("#ifndef", "#ifndef", "#ifndef block",
                "#ifndef ${1:Name}\n   $0\n#endif");

            // ── Utility / special ─────────────────────────────────────────────
            Add("start", "start", "FUNCTION Start()",
                "FUNCTION Start( cmdLineArgs AS STRING[] ) AS INT\n   LOCAL exitCode AS INT\n   $0\n   RETURN exitCode");

            Add("initproc", "initproc", "INIT procedure",
                "PROCEDURE ${1:MyInitProc}() AS VOID _INIT3\n   $0\nRETURN");

            Add("exitproc", "exitproc", "EXIT procedure",
                "PROCEDURE ${1:MyExitProc}() AS VOID EXIT\n   $0\nRETURN");

            Add("nunit", "nunit", "NUnit test class",
                "[TestFixture, Category( \"${1:TestCategory}\" ) ];\nCLASS ${2:TestClass}\n\n   [Test];\n   METHOD Test1() AS VOID\n      $0\n      RETURN\n\nEND CLASS");

            Add("fhdr", "fhdr", "File header comment",
                "/*//////////////////////////////////////////////////////////////////////////////\n*\n* File: $0\n* Created:\n* Created by:\n* Description:\n*\n*/");

            Add("mbox", "mbox", "MessageBox.Show()",
                "MessageBox.Show( ${1:\"Test\"} )$0");

            return items.ToImmutable();
        }

        // ====================================================================
        // INHERIT / IMPLEMENTS context detection
        // ====================================================================

        private enum InheritContext { None, Inherit, Implements }

        /// <summary>
        /// Checks whether the cursor sits in an INHERIT or IMPLEMENTS clause on the
        /// current line.  Strips the word being typed and any previously typed
        /// comma-separated type names so that "CLASS Foo IMPLEMENTS IBar, |" is
        /// correctly recognised as an IMPLEMENTS context.
        /// </summary>
        private InheritContext DetectInheritContext(DocumentUri uri, Position cursor)
        {
            if (!_documentService.TryGetText(uri, out var text))
                return InheritContext.None;

            var lines = text.Split('\n');
            if (cursor.Line >= lines.Length) return InheritContext.None;

            string line = lines[cursor.Line];
            int col = Math.Min((int)cursor.Character, line.Length);

            // Strip the word currently being typed.
            int end = col;
            while (end > 0 && IsWordChar(line[end - 1])) end--;
            string before = line.Substring(0, end);

            // Strip trailing comma-separated type/namespace identifiers.
            // Each iteration removes one "  ,  TypeName" from the right.
            while (true)
            {
                before = before.TrimEnd();
                if (before.Length == 0) break;
                if (before[before.Length - 1] != ',') break;

                // Drop the comma, then the identifier before it.
                before = before.Substring(0, before.Length - 1).TrimEnd();
                int i = before.Length;
                while (i > 0 && (IsWordChar(before[i - 1]) || before[i - 1] == '.')) i--;
                before = before.Substring(0, i);
            }

            before = before.TrimEnd();
            if (EndsWithKeyword(before, "INHERIT"))    return InheritContext.Inherit;
            if (EndsWithKeyword(before, "IMPLEMENTS")) return InheritContext.Implements;
            return InheritContext.None;
        }

        private static bool EndsWithKeyword(string text, string keyword)
        {
            if (!text.EndsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
            int offset = text.Length - keyword.Length;
            return offset == 0 || (!char.IsLetterOrDigit(text[offset - 1]) && text[offset - 1] != '_');
        }

        // ====================================================================
        // DB helpers
        // ====================================================================

        /// <summary>
        /// Scans leftward from <paramref name="cursor"/> to detect a member-access
        /// expression such as <c>identifier:</c>, <c>GetFoo():</c>, or a deeper
        /// chain like <c>oObj:GetFoo():GetBar():</c>.
        /// Returns the full chain text (e.g. <c>"oObj:GetFoo():GetBar()"</c>) so
        /// <see cref="XSharpTypeResolver.Resolve"/> can walk it segment by segment,
        /// or <c>null</c> when no member-access expression precedes the cursor.
        /// </summary>
        private string? GetMemberAccessType(DocumentUri uri, Position cursor)
        {
            if (!_documentService.TryGetText(uri, out var text))
                return null;

            int offset = GetOffset(text, cursor.Line, cursor.Character);
            if (offset < 2) return null;

            // The character immediately before the cursor should be '.' or ':'
            char trigger = text[offset - 1];
            if (trigger != '.' && trigger != ':') return null;

            // Skip any whitespace between trigger and what precedes it.
            int beforeTrigger = offset - 2;
            while (beforeTrigger >= 0 && char.IsWhiteSpace(text[beforeTrigger]))
                beforeTrigger--;

            if (beforeTrigger < 0) return null;

            int chainStart = FindChainStart(text, beforeTrigger);
            if (chainStart < 0) return null;

            return text.Substring(chainStart, beforeTrigger - chainStart + 1);
        }

        /// <summary>
        /// Walks backward from <paramref name="end"/> (inclusive) over a sequence of
        /// <c>:</c>/<c>.</c>-separated segments — each either a plain identifier
        /// (<c>Foo</c>) or a call with balanced parentheses (<c>Foo(args)</c>) —
        /// and returns the start offset of the whole chain, or <c>-1</c> when
        /// <paramref name="end"/> is not the end of a recognisable chain segment.
        /// </summary>
        private static int FindChainStart(string text, int end)
        {
            int pos = end;
            int chainStart = -1;

            while (pos >= 0)
            {
                // ── Call segment: Name(args) — skip the balanced (...) part ──
                if (text[pos] == ')')
                {
                    int depth = 1;
                    int p = pos - 1;
                    while (p >= 0 && depth > 0)
                    {
                        if      (text[p] == ')') depth++;
                        else if (text[p] == '(') depth--;
                        p--;
                    }
                    if (depth != 0) break;   // unbalanced — not a chain we recognise
                    pos = p;
                    while (pos >= 0 && char.IsWhiteSpace(text[pos])) pos--;
                }

                if (pos < 0 || !IsWordChar(text[pos])) break;

                int identEnd = pos;
                while (pos >= 0 && IsWordChar(text[pos])) pos--;
                int identStart = pos + 1;
                if (identStart > identEnd) break;

                chainStart = identStart;

                // Is there a chain separator ('.' or ':') before this segment?
                int sep = pos;
                while (sep >= 0 && char.IsWhiteSpace(text[sep])) sep--;
                if (sep >= 0 && (text[sep] == ':' || text[sep] == '.'))
                {
                    pos = sep - 1;
                    continue;
                }

                break;
            }

            return chainStart;
        }

        /// <summary>
        /// Converts a <see cref="Models.WorkspaceSymbol"/> into a <see cref="CompletionItem"/>.
        /// The sort text is prefixed with <c>~</c> so cross-file items appear after
        /// keyword and in-file items.
        /// </summary>
        private static CompletionItem SymbolToCompletionItem(Models.WorkspaceSymbol sym)
        {
            var kind = sym.Kind switch
            {
                1  => CompletionItemKind.Class,       // Class
                2  => CompletionItemKind.Method,      // Method
                3  => CompletionItemKind.Property,    // Access/Assign
                4  => CompletionItemKind.Field,       // Field / iVar
                5  => CompletionItemKind.Function,    // Function
                6  => CompletionItemKind.Function,    // Procedure
                7  => CompletionItemKind.Variable,    // Global
                8  => CompletionItemKind.Interface,   // Interface
                9  => CompletionItemKind.Struct,      // Structure
                10 => CompletionItemKind.Enum,        // Enum
                11 => CompletionItemKind.EnumMember,  // Enum member
                _  => CompletionItemKind.Value,
            };

            return new CompletionItem
            {
                Label            = sym.Name,
                Kind             = kind,
                Detail           = sym.ReturnType ?? sym.FileName,
                Documentation    = sym.XmlComments != null
                    ? new StringOrMarkupContent(sym.XmlComments)
                    : null,
                SortText         = "~" + sym.Name,   // sorts after keywords/in-file items
                InsertText       = sym.Name,
            };
        }
    }
}

