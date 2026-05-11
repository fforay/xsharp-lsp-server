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

namespace XSharpLanguageServer
{
    /// <summary>
    /// Handles the <c>textDocument/completion</c> LSP request.
    /// <para>
    /// Provides two categories of completion items:
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
    ///     These complement the keyword list so the user can quickly jump to any name
    ///     visible in the file.
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// The prefix is extracted from the document text at the cursor position by scanning
    /// backwards for the start of the current word.
    /// </para>
    /// </summary>
    public class XSharpCompletionHandler : CompletionHandlerBase
    {
        private readonly XSharpDocumentService _documentService;
        private readonly ILogger<XSharpCompletionHandler> _logger;

        /// <summary>
        /// Keyword completion items built once from the lexer vocabulary.
        /// Immutable after construction — safe to share across all requests.
        /// </summary>
        private static readonly ImmutableArray<CompletionItem> _keywordItems =
            BuildKeywordItems();

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpCompletionHandler(
            XSharpDocumentService documentService,
            ILogger<XSharpCompletionHandler> logger)
        {
            _documentService = documentService;
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

                // ----------------------------------------------------------------
                // 1. Keywords — filter by prefix (case-insensitive).
                // ----------------------------------------------------------------
                foreach (var kw in _keywordItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (prefix.Length == 0
                        || kw.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(kw);
                    }
                }

                // ----------------------------------------------------------------
                // 2. Document symbols — identifiers from the current parse tree.
                // ----------------------------------------------------------------
                if (_documentService.TryGetParsed(request.TextDocument.Uri, out var parsed)
                    && parsed.Tree != null)
                {
                    CollectSymbolItems(parsed.Tree, prefix, items, cancellationToken);
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
    }
}
