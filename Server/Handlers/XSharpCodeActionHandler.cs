using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles <c>textDocument/codeAction</c>.
    /// <para>
    /// Currently offers one source action:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Fix all keyword casing</b> (<c>source.fixAll</c>) — scans every
    ///     keyword token in the open document and emits one
    ///     <see cref="TextEdit"/> per token whose text does not match the
    ///     canonical UPPER-CASE spelling.  Indentation and all other content
    ///     is untouched (unlike <c>textDocument/formatting</c>).
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// Planned but deferred: <b>Add USING namespace</b> — requires the
    /// XSharp IntelliSense DB to expose a <c>Namespace</c> column on
    /// <c>ReferencedTypes</c> so the correct namespace can be determined
    /// from the type name at the cursor.
    /// </para>
    /// </summary>
    public class XSharpCodeActionHandler : CodeActionHandlerBase
    {
        private readonly XSharpDocumentService   _documentService;
        private readonly XSharpDatabaseService   _dbService;
        private readonly ILogger<XSharpCodeActionHandler> _logger;

        // Matches a USING statement line (case-insensitive).
        private static readonly Regex _usingPattern =
            new(@"^\s*USING\s+[\w.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public XSharpCodeActionHandler(
            XSharpDocumentService   documentService,
            XSharpDatabaseService   dbService,
            ILogger<XSharpCodeActionHandler> logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
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
                    CodeActionKind.SourceFixAll),
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

                // ── Add USING namespace ───────────────────────────────────
                if (_documentService.TryGetText(uri, out var text))
                {
                    string word = ExtractWord(text, request.Range.Start);
                    if (!string.IsNullOrEmpty(word))
                    {
                        var addUsingActions = ComputeAddUsingActions(uri, text, word);
                        items.AddRange(addUsingActions);
                    }
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
        // Add USING namespace
        // ====================================================================

        private List<CommandOrCodeAction> ComputeAddUsingActions(
            DocumentUri uri, string text, string typeName)
        {
            var results = new List<CommandOrCodeAction>();
            if (!_dbService.IsAvailable) return results;

            var ns = _dbService.FindAssemblyTypeNamespace(typeName);
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

            if (!_documentService.TryGetParsed(uri, out var parsed)) return edits;
            if (parsed.TokenStream is not BufferedTokenStream stream) return edits;

            stream.Fill();
            var tokens = stream.GetTokens();
            if (tokens == null) return edits;

            var keywordMap = XSharpFormattingHandler.KeywordMap;

            foreach (var token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (token.Channel != 0) continue;           // hidden channel
                if (token.Type == -1)   continue;           // EOF
                if (XSharpLexer.IsString(token.Type))  continue;
                if (XSharpLexer.IsComment(token.Type)) continue;

                if (!keywordMap.TryGetValue(token.Type, out var canonical)) continue;
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
