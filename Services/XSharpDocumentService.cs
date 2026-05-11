using LanguageService.CodeAnalysis.Text;
using LanguageService.CodeAnalysis.XSharp;
using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using XSharp.Parser;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Immutable snapshot of the result of parsing a single XSharp document.
    /// A new instance is created and stored in the cache after every re-parse.
    /// All properties are safe to read from any thread once the instance is published.
    /// </summary>
    public sealed class ParsedDocument
    {
        /// <summary>The source text that was parsed to produce this result.</summary>
        public string Text { get; }

        /// <summary>
        /// The full token stream produced by <see cref="VsParser.Parse"/>.
        /// Includes tokens on all channels (code, hidden, comments, preprocessor).
        /// May be <c>null</c> if parsing failed with an unhandled exception.
        /// </summary>
        public ITokenStream? TokenStream { get; }

        /// <summary>
        /// The ANTLR4 parse tree root produced by <see cref="VsParser.Parse"/>.
        /// Walk this tree to extract symbols, build outlines, or resolve references.
        /// May be <c>null</c> if parsing failed with an unhandled exception.
        /// </summary>
        public XSharpParserRuleContext? Tree { get; }

        /// <summary>
        /// LSP <see cref="Diagnostic"/> objects collected during parsing.
        /// Errors and warnings are reported to the client via
        /// <c>textDocument/publishDiagnostics</c> immediately after each parse.
        /// </summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        /// <summary>
        /// Paths of all files transitively pulled in via <c>#include</c> directives
        /// during preprocessing. Useful for tracking file dependencies.
        /// </summary>
        public IReadOnlyList<string> IncludeFiles { get; }

        /// <summary>Initialises a fully populated parse result.</summary>
        public ParsedDocument(
            string text,
            ITokenStream? tokenStream,
            XSharpParserRuleContext? tree,
            IReadOnlyList<Diagnostic> diagnostics,
            IReadOnlyList<string> includeFiles)
        {
            Text = text;
            TokenStream = tokenStream;
            Tree = tree;
            Diagnostics = diagnostics;
            IncludeFiles = includeFiles;
        }
    }

    /// <summary>
    /// Central singleton service that owns the per-document state for the LSP server.
    /// <para>
    /// Responsibilities:
    /// <list type="bullet">
    ///   <item>Maintains a raw text buffer (one entry per open document).</item>
    ///   <item>Triggers a full re-parse via <see cref="VsParser.Parse"/> whenever
    ///         a document is opened, changed, or saved.</item>
    ///   <item>Stores the resulting <see cref="ParsedDocument"/> (token stream,
    ///         parse tree, diagnostics) so all handlers share one parse per document.</item>
    ///   <item>Forwards diagnostics to <see cref="XSharpDiagnosticsPublisher"/> after
    ///         each successful parse.</item>
    /// </list>
    /// </para>
    /// All public methods are thread-safe.
    /// </summary>
    public sealed class XSharpDocumentService
    {
        private readonly ILogger<XSharpDocumentService> _logger;

        /// <summary>
        /// Optional publisher wired in after server startup.
        /// Kept nullable to break the circular DI dependency:
        /// <see cref="XSharpDiagnosticsPublisher"/> needs <c>ILanguageServerFacade</c>,
        /// which is only resolvable after the OmniSharp server has been fully built.
        /// </summary>
        private XSharpDiagnosticsPublisher? _diagnosticsPublisher;

        // Two separate dictionaries so handlers can read text cheaply
        // without touching the (heavier) parse result.
        private readonly Dictionary<DocumentUri, string> _texts = new();
        private readonly Dictionary<DocumentUri, ParsedDocument> _parsed = new();

        /// <summary>
        /// Single lock that protects both <c>_texts</c> and <c>_parsed</c>.
        /// Parsing itself runs outside the lock to avoid blocking readers.
        /// </summary>
        private readonly object _lock = new();

        /// <summary>
        /// Initialises the service. Called by the DI container.
        /// </summary>
        public XSharpDocumentService(ILogger<XSharpDocumentService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Wires in the diagnostics publisher after the OmniSharp server is fully started.
        /// Must be called once from <c>Program.cs</c> before any documents are opened.
        /// </summary>
        /// <param name="publisher">The singleton publisher to notify after each parse.</param>
        public void SetDiagnosticsPublisher(XSharpDiagnosticsPublisher publisher)
        {
            _diagnosticsPublisher = publisher;
        }

        // ----------------------------------------------------------------
        // Text buffer management
        // ----------------------------------------------------------------

        /// <summary>
        /// Stores the new text for <paramref name="uri"/> and immediately triggers
        /// a background re-parse. Called on every DidOpen, DidChange, and DidSave.
        /// </summary>
        public void UpdateText(DocumentUri uri, string text)
        {
            lock (_lock)
            {
                _texts[uri] = text;
            }
            // Parse outside the lock so readers are not blocked while VsParser runs.
            ParseDocument(uri, text);
        }

        /// <summary>
        /// Returns the current raw text for <paramref name="uri"/>.
        /// </summary>
        /// <returns><c>true</c> if the document is currently open, <c>false</c> otherwise.</returns>
        public bool TryGetText(DocumentUri uri, out string text)
        {
            lock (_lock)
            {
                return _texts.TryGetValue(uri, out text!);
            }
        }

        /// <summary>
        /// Removes all state for <paramref name="uri"/>. Called on DidClose.
        /// </summary>
        public void Remove(DocumentUri uri)
        {
            lock (_lock)
            {
                _texts.Remove(uri);
                _parsed.Remove(uri);
            }
        }

        // ----------------------------------------------------------------
        // Parse cache access
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the most recent parse result for <paramref name="uri"/>.
        /// The result may be slightly stale if a re-parse is in progress.
        /// </summary>
        /// <returns><c>true</c> if a result is available, <c>false</c> if the document
        /// has not been parsed yet.</returns>
        public bool TryGetParsed(DocumentUri uri, out ParsedDocument parsed)
        {
            lock (_lock)
            {
                return _parsed.TryGetValue(uri, out parsed!);
            }
        }

        // ----------------------------------------------------------------
        // Parsing
        // ----------------------------------------------------------------

        /// <summary>
        /// Runs <see cref="VsParser.Parse"/> on <paramref name="text"/>, stores the
        /// result in the cache, and notifies the diagnostics publisher.
        /// <para>
        /// Parsing uses <see cref="XSharpParseOptions.Default"/>, which selects the
        /// Core dialect with no additional preprocessor symbols. Future work can make
        /// options configurable via LSP workspace settings.
        /// </para>
        /// </summary>
        private void ParseDocument(DocumentUri uri, string text)
        {
            try
            {
                string fileName = uri.GetFileSystemPath() ?? uri.ToString();

                // Default options: Core dialect, ParseLevel.Complete.
                // TODO: expose dialect and include paths as configurable LSP settings.
                var options = XSharpParseOptions.Default;

                var errorListener = new ErrorListener(fileName);

                // VsParser.Parse performs: lex → preprocess → parse.
                // It returns the raw token stream (all channels) and the ANTLR4 tree.
                bool ok = VsParser.Parse(
                    text,
                    fileName,
                    options,
                    errorListener,
                    out var tokenStream,
                    out var tree,
                    out var includeFiles);

                var diagnostics = errorListener.Diagnostics;

                _logger.LogInformation(
                    "Parsed {Uri}: ok={Ok}, tokens={Tokens}, diagnostics={Diag}",
                    uri,
                    ok,
                    (tokenStream as BufferedTokenStream)?.GetTokens().Count ?? 0,
                    diagnostics.Count);

                var result = new ParsedDocument(
                    text,
                    tokenStream,
                    tree,
                    diagnostics,
                    includeFiles ?? new List<string>());

                lock (_lock)
                {
                    _parsed[uri] = result;
                }

                // Push diagnostics to the client. Safe to call with an empty list
                // (clears any previously shown squiggles).
                _diagnosticsPublisher?.Publish(uri, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Parse failed for {Uri}", uri);

                // Store a minimal fallback so handlers never have to deal with a
                // missing cache entry — they will simply get no tokens and no diagnostics.
                var fallback = new ParsedDocument(
                    text,
                    tokenStream: null,
                    tree: null,
                    diagnostics: Array.Empty<Diagnostic>(),
                    includeFiles: Array.Empty<string>());

                lock (_lock)
                {
                    _parsed[uri] = fallback;
                }
            }
        }

        // ----------------------------------------------------------------
        // Inner error listener — collects VsParser errors into LSP Diagnostics
        // ----------------------------------------------------------------

        /// <summary>
        /// Private implementation of <see cref="VsParser.IErrorListener"/> that
        /// accumulates errors and warnings reported by the XSharp lexer/parser and
        /// converts them into LSP <see cref="Diagnostic"/> objects.
        /// </summary>
        private sealed class ErrorListener : VsParser.IErrorListener
        {
            private readonly string _fileName;
            private readonly List<Diagnostic> _diagnostics = new();

            /// <summary>All diagnostics collected since this instance was created.</summary>
            public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

            public ErrorListener(string fileName)
            {
                _fileName = fileName;
            }

            /// <inheritdoc/>
            public void ReportError(string fileName, LinePositionSpan span,
                string errorCode, string message, object[] args)
            {
                _diagnostics.Add(BuildDiagnostic(
                    span, errorCode, message, args, DiagnosticSeverity.Error));
            }

            /// <inheritdoc/>
            public void ReportWarning(string fileName, LinePositionSpan span,
                string errorCode, string message, object[] args)
            {
                _diagnostics.Add(BuildDiagnostic(
                    span, errorCode, message, args, DiagnosticSeverity.Warning));
            }

            /// <summary>
            /// Converts a <see cref="LinePositionSpan"/> and error details into an
            /// LSP <see cref="Diagnostic"/>.
            /// </summary>
            private static Diagnostic BuildDiagnostic(
                LinePositionSpan span,
                string errorCode,
                string message,
                object[] args,
                DiagnosticSeverity severity)
            {
                // XSharp lines are 1-based; LSP positions are 0-based.
                int startLine = Math.Max(0, span.Line - 1);
                int startChar = Math.Max(0, span.Column);

                var range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(startLine, startChar),
                    new Position(startLine, startChar + 1));

                // Format the message only if there are arguments to substitute.
                string formattedMessage = args?.Length > 0
                    ? string.Format(message, args)
                    : message;

                return new Diagnostic
                {
                    Range = range,
                    Severity = severity,
                    Code = new DiagnosticCode(errorCode),
                    Source = "xsharp",
                    Message = formattedMessage
                };
            }
        }
    }
}
