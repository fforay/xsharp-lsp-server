using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles <c>textDocument/formatting</c> — formats an entire XSharp source file.
    /// <para>
    /// Two transforms are applied:
    /// <list type="number">
    ///   <item><b>Keyword casing</b> — every keyword token (type 1–223) is replaced
    ///     with its canonical UPPER-CASE spelling derived from the
    ///     <see cref="XSharpLexer"/> field names.</item>
    ///   <item><b>Indentation</b> — leading whitespace on each line is replaced with
    ///     <c>indentLevel × tabSize</c> spaces (or hard tabs when
    ///     <see cref="FormattingOptions.InsertSpaces"/> is <c>false</c>).
    ///     Indent level is tracked by counting block-open / block-close tokens.</item>
    /// </list>
    /// A single <see cref="TextEdit"/> replacing the whole document is returned so the
    /// client applies the change atomically.
    /// </para>
    /// </summary>
    public class XSharpFormattingHandler : DocumentFormattingHandlerBase
    {
        // ----------------------------------------------------------------
        // Keyword map: tokenType (1-223) → canonical uppercase spelling
        // Built once from XSharpLexer public static int fields.
        // ----------------------------------------------------------------
        private static readonly IReadOnlyDictionary<int, string> _keywordMap =
            BuildKeywordMap();

        private static Dictionary<int, string> BuildKeywordMap()
        {
            var map = new Dictionary<int, string>();
            // Channel pseudo-constants that collide with keyword range — skip these.
            var skip = new HashSet<string>(StringComparer.Ordinal)
            {
                "FIRST_KEYWORD", "LAST_KEYWORD",
                "XMLDOCCHANNEL", "DEFOUTCHANNEL", "PREPROCESSORCHANNEL",
            };

            foreach (FieldInfo fi in typeof(XSharpLexer).GetFields(
                         BindingFlags.Public | BindingFlags.Static))
            {
                if (fi.FieldType != typeof(int)) continue;
                if (skip.Contains(fi.Name)) continue;

                int value = (int)fi.GetValue(null)!;
                if (value < 1 || value > 223) continue;

                // First field wins when two names share the same value.
                if (!map.ContainsKey(value))
                    map[value] = fi.Name;   // e.g. "IF", "FUNCTION", "CLASS" …
            }
            return map;
        }

        // ----------------------------------------------------------------
        // Token types used for indent tracking
        // ----------------------------------------------------------------

        /// <summary>
        /// Code-block header tokens: FUNCTION/PROCEDURE/METHOD/CONSTRUCTOR/DESTRUCTOR/
        /// PROPERTY/OPERATOR/EVENT/ACCESS/ASSIGN.
        /// When one of these is encountered it implicitly closes the previous code block
        /// (if any) before opening a new one.
        /// </summary>
        private static readonly HashSet<int> _codeBlockHeaders = new()
        {
            XSharpLexer.FUNCTION,   XSharpLexer.PROCEDURE,
            XSharpLexer.METHOD,     XSharpLexer.CONSTRUCTOR, XSharpLexer.DESTRUCTOR,
            XSharpLexer.PROPERTY,   XSharpLexer.OPERATOR,    XSharpLexer.EVENT,
            XSharpLexer.ACCESS,     XSharpLexer.ASSIGN,
        };

        /// <summary>
        /// Type-opener tokens (CLASS/INTERFACE/STRUCTURE).
        /// Entering one resets the <c>inMember</c> flag — the body of a type
        /// is not itself a code block.
        /// </summary>
        private static readonly HashSet<int> _typeOpeners = new()
        {
            XSharpLexer.CLASS, XSharpLexer.INTERFACE, XSharpLexer.STRUCTURE,
        };

        /// <summary>
        /// Single-token explicit type-closers (e.g. the <c>ENDCLASS</c> keyword).
        /// Two-token forms such as <c>END CLASS</c> are detected by <see cref="IsTypeCloser"/>.
        /// </summary>
        private static readonly HashSet<int> _typeClosers = new()
        {
            XSharpLexer.ENDCLASS,
        };

        /// <summary>
        /// Single-token explicit member-end tokens (ENDFUNC, ENDPROC).
        /// Two-token forms such as <c>END METHOD</c> are detected by <see cref="IsEndMember"/>.
        /// </summary>
        private static readonly HashSet<int> _explicitMemberEnds = new()
        {
            XSharpLexer.ENDFUNC, XSharpLexer.ENDPROC,
        };

        /// <summary>Token types that open a new indented block (control flow + type declarations).</summary>
        private static readonly HashSet<int> _indentOpen = new()
        {
            XSharpLexer.CLASS,      XSharpLexer.INTERFACE,  XSharpLexer.STRUCTURE,
            XSharpLexer.IF,         XSharpLexer.ELSEIF,     XSharpLexer.ELSE,
            XSharpLexer.DO,         XSharpLexer.FOR,        XSharpLexer.FOREACH,
            XSharpLexer.WHILE,      XSharpLexer.REPEAT,
            XSharpLexer.BEGIN,
            XSharpLexer.TRY,        XSharpLexer.CATCH,      XSharpLexer.FINALLY,
            XSharpLexer.SWITCH,     XSharpLexer.CASE,       XSharpLexer.OTHERWISE,
            XSharpLexer.WITH,
            // PROPERTY sub-block accessors
            XSharpLexer.GET,        XSharpLexer.SET,
        };

        /// <summary>
        /// PROPERTY is a single-line (non-block) declaration when the same line also carries
        /// one of these tokens: an inline getter/setter or the AUTO keyword.
        /// </summary>
        private static readonly HashSet<int> _singleLinePropertyMarkers = new()
        {
            XSharpLexer.GET, XSharpLexer.SET, XSharpLexer.AUTO,
        };

        /// <summary>
        /// Sub-block openers whose two-token <c>END X</c> form requires a simple close (−1)
        /// without touching <c>inMember</c>.  Detected by <see cref="IsSubBlockEnd"/>.
        /// </summary>
        private static readonly HashSet<int> _subBlockOpeners = new()
        {
            XSharpLexer.GET, XSharpLexer.SET,
        };

        /// <summary>
        /// Token types that close the current control-flow block before this line is written.
        /// Code-block headers (METHOD, FUNCTION, …) and type-closers (ENDCLASS, END CLASS, …)
        /// are handled separately.
        /// </summary>
        private static readonly HashSet<int> _indentClose = new()
        {
            XSharpLexer.ENDIF,      XSharpLexer.ENDDO,      XSharpLexer.ENDCASE,
            XSharpLexer.ENDFOR,     XSharpLexer.ENDSCAN,    XSharpLexer.ENDTRY,
            XSharpLexer.ENDWITH,
            XSharpLexer.ELSE,       XSharpLexer.ELSEIF,
            XSharpLexer.CATCH,      XSharpLexer.FINALLY,
            XSharpLexer.CASE,       XSharpLexer.OTHERWISE,
            XSharpLexer.NEXT,
        };

        // ----------------------------------------------------------------
        // Fields
        // ----------------------------------------------------------------
        private readonly XSharpDocumentService              _documentService;
        private readonly ILogger<XSharpFormattingHandler>   _logger;

        public XSharpFormattingHandler(
            XSharpDocumentService               documentService,
            ILogger<XSharpFormattingHandler>    logger)
        {
            _documentService = documentService;
            _logger          = logger;
        }

        // ----------------------------------------------------------------
        // Registration
        // ----------------------------------------------------------------
        protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
            DocumentFormattingCapability capability,
            ClientCapabilities           clientCapabilities)
            => new DocumentFormattingRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        // ----------------------------------------------------------------
        // Handler
        // ----------------------------------------------------------------
        public override Task<TextEditContainer?> Handle(
            DocumentFormattingParams request,
            CancellationToken        cancellationToken)
        {
            try
            {
                var uri = request.TextDocument.Uri;

                if (!_documentService.TryGetText(uri, out var originalText))
                    return Task.FromResult<TextEditContainer?>(null);

                if (!_documentService.TryGetParsed(uri, out var parsed))
                    return Task.FromResult<TextEditContainer?>(null);

                if (parsed.TokenStream is not BufferedTokenStream stream)
                    return Task.FromResult<TextEditContainer?>(null);

                // Make sure all tokens including hidden-channel are available.
                stream.Fill();
                var allTokens = stream.GetTokens();
                if (allTokens == null || allTokens.Count == 0)
                    return Task.FromResult<TextEditContainer?>(null);

                int  tabSize      = (int)(request.Options.TabSize > 0 ? request.Options.TabSize : 4);
                bool insertSpaces = request.Options.InsertSpaces;
                string indentUnit = insertSpaces ? new string(' ', tabSize) : "\t";

                string formatted = Format(originalText, allTokens, indentUnit);

                if (formatted == originalText)
                    return Task.FromResult<TextEditContainer?>(null);   // nothing changed

                // Replace entire document.
                var lines       = originalText.Split('\n');
                int lastLine    = lines.Length - 1;
                int lastCol     = lines[lastLine].Length;

                var edit = new TextEdit
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(0, 0),
                        new Position(lastLine, lastCol)),
                    NewText = formatted,
                };

                _logger.LogInformation("Formatting: applied to {Uri}", uri);
                return Task.FromResult<TextEditContainer?>(new TextEditContainer(edit));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Formatting failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<TextEditContainer?>(null);
            }
        }

        // ----------------------------------------------------------------
        // Core formatting logic
        // ----------------------------------------------------------------
        private string Format(
            string        originalText,
            IList<IToken> tokens,
            string        indentUnit)
        {
            // Split original into lines (preserve \r\n vs \n).
            bool hasCr = originalText.Contains('\r');

            // Build a per-line list of tokens (using 1-based line from lexer).
            // Hidden/WS tokens carry whitespace that we will reconstruct.
            // We only care about non-hidden tokens for logic; we rebuild spacing.

            // Group tokens by line number (1-based).
            var byLine = new Dictionary<int, List<IToken>>();
            foreach (var t in tokens)
            {
                if (t.Type == -1) continue;   // EOF
                int ln = t.Line;
                if (!byLine.TryGetValue(ln, out var list))
                    byLine[ln] = list = new List<IToken>();
                list.Add(t);
            }

            // Determine max line number.
            int maxLine = 0;
            foreach (var k in byLine.Keys)
                if (k > maxLine) maxLine = k;

            var sb             = new StringBuilder(originalText.Length);
            int indentLevel    = 0;
            bool inMember      = false;  // true once a code-block header has been seen
            bool memberBodyOpen = false; // true only when indentLevel was incremented for that member
            string nl          = hasCr ? "\r\n" : "\n";

            var origLines = originalText.Split('\n');

            for (int ln = 1; ln <= maxLine; ln++)
            {
                string origLine = ln - 1 < origLines.Length
                    ? origLines[ln - 1].TrimEnd('\r')
                    : string.Empty;

                if (!byLine.TryGetValue(ln, out var lineTokens) || lineTokens.Count == 0)
                {
                    sb.Append(nl);
                    continue;
                }

                // First non-hidden, non-comment, non-string token on this line.
                IToken? firstReal = null;
                foreach (var t in lineTokens)
                {
                    if (t.Channel == 0 && t.Type != -1
                        && !XSharpLexer.IsComment(t.Type)
                        && !XSharpLexer.IsString(t.Type))
                    { firstReal = t; break; }
                }

                bool isCodeBlockHeader  = firstReal != null && _codeBlockHeaders.Contains(firstReal.Type);
                bool isTypeOpener       = firstReal != null && _typeOpeners.Contains(firstReal.Type);
                bool isTypeCloser       = firstReal != null && IsTypeCloser(firstReal, lineTokens);
                bool isEndMember        = !isTypeCloser && firstReal != null && IsEndMember(firstReal, lineTokens);
                bool isSubBlockEnd      = !isTypeCloser && !isEndMember
                                          && firstReal != null && IsSubBlockEnd(firstReal, lineTokens);
                // Any remaining END + X: generic one-level close (e.g. END SWITCH).
                bool isGenericEnd       = !isTypeCloser && !isEndMember && !isSubBlockEnd
                                          && firstReal != null && firstReal.Type == XSharpLexer.END
                                          && GetNextRealType(firstReal, lineTokens) != -1;
                // PROPERTY on one line with GET/SET/AUTO — member marker but no body block.
                bool isSingleLineMember = isCodeBlockHeader
                    && firstReal!.Type == XSharpLexer.PROPERTY
                    && LineContainsAny(lineTokens, _singleLinePropertyMarkers);

                // ---- Adjust indent BEFORE writing ----
                if (isTypeCloser)
                {
                    // Close the open member body (if any), then close the type.
                    if (inMember && memberBodyOpen) indentLevel = Math.Max(0, indentLevel - 1);
                    inMember = false; memberBodyOpen = false;
                    indentLevel = Math.Max(0, indentLevel - 1);
                }
                else if (isTypeOpener && inMember)
                {
                    // CLASS/INTERFACE/STRUCTURE after an open member: implicitly close member body.
                    if (memberBodyOpen) indentLevel = Math.Max(0, indentLevel - 1);
                    inMember = false; memberBodyOpen = false;
                }
                else if (isCodeBlockHeader && inMember && memberBodyOpen)
                {
                    // Implicit close of previous code block body.
                    indentLevel = Math.Max(0, indentLevel - 1);
                }
                else if (isEndMember)
                {
                    indentLevel = Math.Max(0, indentLevel - 1);
                    inMember = false; memberBodyOpen = false;
                }
                else if (isSubBlockEnd || isGenericEnd)
                {
                    indentLevel = Math.Max(0, indentLevel - 1);
                }
                else if (firstReal != null && _indentClose.Contains(firstReal.Type))
                {
                    indentLevel = Math.Max(0, indentLevel - 1);
                }

                // ---- Write line ----
                string lineContent = RebuildLine(origLine, lineTokens);
                string indent      = BuildIndent(indentLevel, indentUnit);
                sb.Append(indent);
                sb.Append(lineContent.TrimStart());

                if (ln < maxLine) sb.Append(nl);

                // ---- Adjust indent AFTER writing ----
                if (isCodeBlockHeader)
                {
                    inMember = true;
                    if (!isSingleLineMember) { indentLevel++; memberBodyOpen = true; }
                    else memberBodyOpen = false;
                }
                else if (!isTypeCloser && !isEndMember && !isSubBlockEnd && !isGenericEnd
                         && firstReal != null && _indentOpen.Contains(firstReal.Type))
                {
                    indentLevel++;
                    if (isTypeOpener) { inMember = false; memberBodyOpen = false; }
                }
            }

            return sb.ToString();
        }

        // ----------------------------------------------------------------
        // Helpers for type-close / member-end detection
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns true when <paramref name="firstReal"/> signals the end of a type block
        /// (CLASS/INTERFACE/STRUCTURE): either a single <c>ENDCLASS</c> token or a two-token
        /// <c>END CLASS</c> / <c>END INTERFACE</c> / <c>END STRUCTURE</c> sequence.
        /// </summary>
        private static bool IsTypeCloser(IToken firstReal, List<IToken> lineTokens)
        {
            if (_typeClosers.Contains(firstReal.Type)) return true;
            if (firstReal.Type == XSharpLexer.END)
            {
                int next = GetNextRealType(firstReal, lineTokens);
                return next == XSharpLexer.CLASS
                    || next == XSharpLexer.INTERFACE
                    || next == XSharpLexer.STRUCTURE;
            }
            return false;
        }

        /// <summary>
        /// Returns true when <paramref name="firstReal"/> explicitly closes a code block
        /// member: either a single <c>ENDFUNC</c>/<c>ENDPROC</c> token or a two-token
        /// <c>END METHOD</c> / <c>END FUNCTION</c> / … sequence.
        /// </summary>
        private static bool IsEndMember(IToken firstReal, List<IToken> lineTokens)
        {
            if (_explicitMemberEnds.Contains(firstReal.Type)) return true;
            if (firstReal.Type == XSharpLexer.END)
            {
                int next = GetNextRealType(firstReal, lineTokens);
                return _codeBlockHeaders.Contains(next);
            }
            return false;
        }

        /// <summary>
        /// Returns the token type of the first real (channel 0, non-EOF) token on the line
        /// that comes after <paramref name="after"/>, or -1 if none.
        /// </summary>
        private static int GetNextRealType(IToken after, List<IToken> lineTokens)
        {
            bool found = false;
            foreach (var t in lineTokens)
            {
                if (!found) { if (t == after) found = true; continue; }
                if (t.Channel == 0 && t.Type != -1) return t.Type;
            }
            return -1;
        }

        /// <summary>
        /// Returns true for <c>END GET</c> / <c>END SET</c> — a two-token form that closes
        /// a PROPERTY accessor sub-block without ending the enclosing member.
        /// </summary>
        private static bool IsSubBlockEnd(IToken firstReal, List<IToken> lineTokens)
        {
            if (firstReal.Type != XSharpLexer.END) return false;
            return _subBlockOpeners.Contains(GetNextRealType(firstReal, lineTokens));
        }

        /// <summary>
        /// Returns true when any token on <paramref name="lineTokens"/> (channel 0) has a
        /// type that is in <paramref name="markers"/>.
        /// </summary>
        private static bool LineContainsAny(List<IToken> lineTokens, HashSet<int> markers)
        {
            foreach (var t in lineTokens)
                if (t.Channel == 0 && markers.Contains(t.Type)) return true;
            return false;
        }

        /// <summary>
        /// Rebuilds the visible content of <paramref name="origLine"/> by replacing
        /// keyword token spans with their canonical uppercase text.
        /// Non-keyword spans are preserved verbatim (string literals, comments, IDs, etc.).
        /// </summary>
        private string RebuildLine(string origLine, List<IToken> lineTokens)
        {
            if (origLine.Length == 0) return origLine;

            // Collect replacements: (startCol, length, newText) — 0-based column.
            var replacements = new List<(int Start, int Len, string New)>();

            foreach (var t in lineTokens)
            {
                if (t.Channel != 0) continue;   // hidden channel (whitespace)
                // Never rewrite string literals or comments — preserve verbatim.
                if (XSharpLexer.IsString(t.Type))  continue;
                if (XSharpLexer.IsComment(t.Type)) continue;
                if (!_keywordMap.TryGetValue(t.Type, out string? canonical)) continue;
                if (string.Equals(t.Text, canonical, StringComparison.Ordinal)) continue;

                int col = t.Column;   // 0-based
                if (col < 0 || col + t.Text.Length > origLine.Length) continue;

                replacements.Add((col, t.Text.Length, canonical));
            }

            if (replacements.Count == 0) return origLine;

            var sb  = new StringBuilder(origLine.Length);
            int pos = 0;
            // Sort by start position ascending.
            replacements.Sort((a, b) => a.Start.CompareTo(b.Start));

            foreach (var (start, len, newText) in replacements)
            {
                if (start > pos)
                    sb.Append(origLine, pos, start - pos);
                sb.Append(newText);
                pos = start + len;
            }
            if (pos < origLine.Length)
                sb.Append(origLine, pos, origLine.Length - pos);

            return sb.ToString();
        }

        private static string BuildIndent(int level, string unit)
        {
            if (level <= 0) return string.Empty;
            var sb = new StringBuilder(level * unit.Length);
            for (int i = 0; i < level; i++) sb.Append(unit);
            return sb.ToString();
        }
    }
}
