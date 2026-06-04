using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XSharp.Parser;
using XSharpLanguageServer.Models;
using XSharpLanguageServer.Services;
using IndexSymbol = XSharpLanguageServer.Models.WorkspaceSymbol;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the call hierarchy LSP requests:
    /// <c>textDocument/prepareCallHierarchy</c>,
    /// <c>callHierarchy/incomingCalls</c>, and
    /// <c>callHierarchy/outgoingCalls</c>.
    /// <para>
    /// <b>Prepare</b> — finds the function/method declaration under the cursor
    /// via the workspace index and returns a <see cref="CallHierarchyItem"/>.
    /// </para>
    /// <para>
    /// <b>Incoming calls</b> — uses the identifier token index
    /// (<see cref="XSharpWorkspaceIndex.FindTokenLocations"/>) to locate every
    /// call site across the project.  A heuristic (last function/method with
    /// <c>StartLine ≤ callLine</c> in the same file) determines the enclosing
    /// caller.  Only locations followed by <c>(</c> are counted as calls.
    /// </para>
    /// <para>
    /// <b>Outgoing calls</b> — parses the source file of the selected function,
    /// walks the parse tree for <see cref="XSharpParser.MethodCallContext"/>
    /// nodes, and looks up each callee in the workspace index.
    /// </para>
    /// </summary>
    public class XSharpCallHierarchyHandler : CallHierarchyHandlerBase
    {
        private readonly XSharpDocumentService    _documentService;
        private readonly XSharpWorkspaceIndex     _workspaceIndex;
        private readonly XSharpConfigurationService _configService;
        private readonly ILogger<XSharpCallHierarchyHandler> _logger;

        public XSharpCallHierarchyHandler(
            XSharpDocumentService              documentService,
            XSharpWorkspaceIndex               workspaceIndex,
            XSharpConfigurationService         configService,
            ILogger<XSharpCallHierarchyHandler> logger)
        {
            _documentService = documentService;
            _workspaceIndex  = workspaceIndex;
            _configService   = configService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override CallHierarchyRegistrationOptions CreateRegistrationOptions(
            CallHierarchyCapability capability,
            ClientCapabilities clientCapabilities)
            => new CallHierarchyRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        // ====================================================================
        // Prepare
        // ====================================================================

        /// <summary>
        /// Finds the function or method declaration under the cursor.
        /// </summary>
        public override Task<Container<CallHierarchyItem>?> Handle(
            CallHierarchyPrepareParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                var uri  = request.TextDocument.Uri;
                var pos  = request.Position;

                if (!_documentService.TryGetText(uri, out var text))
                    return Task.FromResult<Container<CallHierarchyItem>?>(null);

                string word = ExtractWord(text, pos);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<Container<CallHierarchyItem>?>(null);

                string? filePath = uri.GetFileSystemPath();
                var sym = _workspaceIndex.FindExact(word, filePath);

                if (sym == null || !IsCallable(sym.Kind))
                    return Task.FromResult<Container<CallHierarchyItem>?>(null);

                var item = MakeItem(sym);
                _logger.LogInformation("CallHierarchy/prepare: {Name}", word);
                return Task.FromResult<Container<CallHierarchyItem>?>(
                    new Container<CallHierarchyItem>(item));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CallHierarchy/prepare failed");
                return Task.FromResult<Container<CallHierarchyItem>?>(null);
            }
        }

        // ====================================================================
        // Incoming calls
        // ====================================================================

        /// <summary>
        /// Finds all callers of the function represented by <paramref name="request"/>.
        /// </summary>
        public override Task<Container<CallHierarchyIncomingCall>?> Handle(
            CallHierarchyIncomingCallsParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                string name = request.Item.Name;
                _logger.LogInformation("CallHierarchy/incomingCalls: {Name}", name);

                // All token locations where the name appears.
                var locations = _workspaceIndex.FindTokenLocations(name);

                // Group call sites by enclosing function.
                var grouped = new Dictionary<string, (IndexSymbol Caller, List<LspRange> Ranges)>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var tok in locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Filter to actual call sites (followed by '(' on the same line).
                    if (!IsCallSite(tok)) continue;

                    // Find the enclosing function/method in the same file.
                    var caller = FindEnclosingFunction(tok.FilePath, tok.Line);
                    if (caller == null) continue;

                    // Skip recursive self-calls.
                    if (string.Equals(caller.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                    string key = $"{caller.FileName}:{caller.StartLine}";
                    if (!grouped.TryGetValue(key, out var entry))
                    {
                        entry = (caller, new List<LspRange>());
                        grouped[key] = entry;
                    }

                    int endCol = tok.Col + tok.Text.Length;
                    entry.Ranges.Add(new LspRange(
                        new Position(tok.Line, tok.Col),
                        new Position(tok.Line, endCol)));
                }

                var results = grouped.Values
                    .Select(e => new CallHierarchyIncomingCall
                    {
                        From       = MakeItem(e.Caller),
                        FromRanges = new Container<LspRange>(e.Ranges),
                    })
                    .ToList();

                _logger.LogInformation(
                    "CallHierarchy/incomingCalls: {Count} caller(s) for {Name}",
                    results.Count, name);

                return Task.FromResult<Container<CallHierarchyIncomingCall>?>(
                    results.Count > 0 ? new Container<CallHierarchyIncomingCall>(results) : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CallHierarchy/incomingCalls failed");
                return Task.FromResult<Container<CallHierarchyIncomingCall>?>(null);
            }
        }

        // ====================================================================
        // Outgoing calls
        // ====================================================================

        /// <summary>
        /// Finds all functions called by the function represented by
        /// <paramref name="request"/>.
        /// </summary>
        public override Task<Container<CallHierarchyOutgoingCall>?> Handle(
            CallHierarchyOutgoingCallsParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                string name     = request.Item.Name;
                string fileName = request.Item.Uri.GetFileSystemPath() ?? string.Empty;
                int    startLine = (int)(request.Item.Range?.Start.Line ?? 0);

                _logger.LogInformation("CallHierarchy/outgoingCalls: {Name}", name);

                // Get the parse tree for the file — prefer open-document cache.
                var uri  = DocumentUri.FromFileSystemPath(fileName);
                XSharpParserRuleContext? tree = null;
                string? sourceText            = null;

                if (_documentService.TryGetParsed(uri, out var parsed) && parsed.Tree != null)
                {
                    tree       = parsed.Tree;
                    sourceText = parsed.Text;
                }
                else if (File.Exists(fileName))
                {
                    sourceText = File.ReadAllText(fileName);
                    var opts   = _configService.GetParseOptions();
                    VsParser.Parse(sourceText, fileName, opts,
                        new NullErrorListener(), out _, out tree, out _);
                }

                if (tree == null || sourceText == null)
                    return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);

                // Find the function node and walk it for call expressions.
                var calls = new Dictionary<string, (IndexSymbol Callee, List<LspRange> Ranges)>(
                    StringComparer.OrdinalIgnoreCase);

                CollectOutgoingCalls(tree, startLine, name, sourceText, calls, cancellationToken);

                var results = calls.Values
                    .Select(c => new CallHierarchyOutgoingCall
                    {
                        To         = MakeItem(c.Callee),
                        FromRanges = new Container<LspRange>(c.Ranges),
                    })
                    .ToList();

                _logger.LogInformation(
                    "CallHierarchy/outgoingCalls: {Count} callee(s) for {Name}",
                    results.Count, name);

                return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(
                    results.Count > 0 ? new Container<CallHierarchyOutgoingCall>(results) : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CallHierarchy/outgoingCalls failed");
                return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
            }
        }

        // ====================================================================
        // Outgoing call tree walker
        // ====================================================================

        private void CollectOutgoingCalls(
            IParseTree node,
            int funcStartLine,
            string funcName,
            string sourceText,
            Dictionary<string, (IndexSymbol Callee, List<LspRange> Ranges)> calls,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (node is XSharpParser.MethodCallContext mc)
            {
                var calleeText = mc.expression()?.GetText();
                if (!string.IsNullOrEmpty(calleeText))
                {
                    string calleeName = SimpleName(calleeText);
                    if (!string.IsNullOrEmpty(calleeName)
                        && !string.Equals(calleeName, funcName, StringComparison.OrdinalIgnoreCase))
                    {
                        var callee = _workspaceIndex.FindExact(calleeName);
                        if (callee != null && IsCallable(callee.Kind))
                        {
                            int callLine = Math.Max(0, mc.Start.Line - 1);
                            int callCol  = Math.Max(0, mc.Start.Column);
                            int endCol   = callCol + calleeName.Length;

                            if (!calls.TryGetValue(calleeName, out var entry))
                            {
                                entry = (callee, new List<LspRange>());
                                calls[calleeName] = entry;
                            }

                            entry.Ranges.Add(new LspRange(
                                new Position(callLine, callCol),
                                new Position(callLine, endCol)));
                        }
                    }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
                CollectOutgoingCalls(node.GetChild(i), funcStartLine, funcName,
                    sourceText, calls, ct);
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private bool IsCallSite(IdentifierLocation tok)
        {
            // Read the source line to check if the identifier is followed by '('
            try
            {
                string? line = GetSourceLine(tok.FilePath, tok.Line);
                if (line == null) return false;

                int after = tok.Col + tok.Text.Length;
                while (after < line.Length && char.IsWhiteSpace(line[after]))
                    after++;

                return after < line.Length && line[after] == '(';
            }
            catch
            {
                return false;
            }
        }

        private string? GetSourceLine(string filePath, int line)
        {
            // Prefer open-document cache.
            var uri = DocumentUri.FromFileSystemPath(filePath);
            if (_documentService.TryGetText(uri, out var text))
            {
                var lines = text.Split('\n');
                return line < lines.Length ? lines[line].TrimEnd('\r') : null;
            }

            // Fall back to reading from disk.
            if (!File.Exists(filePath)) return null;
            var diskLines = File.ReadAllLines(filePath);
            return line < diskLines.Length ? diskLines[line] : null;
        }

        private IndexSymbol? FindEnclosingFunction(string filePath, int callLine)
        {
            var symbols = _workspaceIndex.GetSymbolsInFile(filePath);

            // Find the last callable symbol whose StartLine ≤ callLine.
            IndexSymbol? best = null;
            foreach (var sym in symbols)
            {
                if (!IsCallable(sym.Kind)) continue;
                if (sym.StartLine > callLine) break;
                best = sym;
            }

            return best;
        }

        private CallHierarchyItem MakeItem(IndexSymbol sym)
        {
            var uri  = DocumentUri.FromFileSystemPath(sym.FileName);
            var range = new LspRange(
                new Position(sym.StartLine, sym.StartCol),
                new Position(sym.StartLine, sym.StartCol + sym.Name.Length));

            return new CallHierarchyItem
            {
                Name            = sym.Name,
                Kind            = KindToSymbolKind(sym.Kind),
                Detail          = sym.TypeName ?? sym.ReturnType,
                Uri             = uri,
                Range           = range,
                SelectionRange  = range,
            };
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

        private static string SimpleName(string expr)
        {
            int colon = expr.LastIndexOf(':');
            int dot   = expr.LastIndexOf('.');
            int sep   = Math.Max(colon, dot);
            return sep >= 0 ? expr[(sep + 1)..] : expr;
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static bool IsCallable(int kind) =>
            kind == XSharpSymbolKind.Function  ||
            kind == XSharpSymbolKind.Procedure ||
            kind == XSharpSymbolKind.Method    ||
            kind == XSharpSymbolKind.Access    ||
            kind == XSharpSymbolKind.Assign    ||
            kind == XSharpSymbolKind.Constructor;

        private static SymbolKind KindToSymbolKind(int kind) => kind switch
        {
            XSharpSymbolKind.Class       => SymbolKind.Class,
            XSharpSymbolKind.Method      => SymbolKind.Method,
            XSharpSymbolKind.Function    => SymbolKind.Function,
            XSharpSymbolKind.Procedure   => SymbolKind.Function,
            XSharpSymbolKind.Constructor => SymbolKind.Constructor,
            XSharpSymbolKind.Access      => SymbolKind.Property,  // Assign == Access == 3
            _                            => SymbolKind.Function,
        };

        private sealed class NullErrorListener : VsParser.IErrorListener
        {
            public void ReportError(string f, LanguageService.CodeAnalysis.Text.LinePositionSpan s,
                string c, string m, object[] a) { }
            public void ReportWarning(string f, LanguageService.CodeAnalysis.Text.LinePositionSpan s,
                string c, string m, object[] a) { }
        }
    }
}
