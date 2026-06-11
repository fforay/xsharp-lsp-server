using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>textDocument/onTypeFormatting</c> LSP request.
    /// <para>
    /// Triggered when the user presses Enter.  If the line that was just
    /// completed is a block-closing keyword (<c>ENDIF</c>, <c>ENDDO</c>,
    /// <c>NEXT</c>, <c>ELSE</c>, <c>ELSEIF</c>, <c>END</c>, <c>ENDCASE</c>,
    /// <c>ENDTRY</c>, <c>CATCH</c>, <c>FINALLY</c>, <c>ENDWITH</c>) the
    /// handler scans upward tracking nesting depth to find the matching opener
    /// and emits a single <see cref="TextEdit"/> that corrects the closing
    /// keyword's leading whitespace to align with its opener.
    /// </para>
    /// </summary>
    public class XSharpOnTypeFormattingHandler : DocumentOnTypeFormattingHandlerBase
    {
        private readonly XSharpDocumentService      _documentService;
        private readonly XSharpConfigurationService _configService;
        private readonly ILogger<XSharpOnTypeFormattingHandler> _logger;

        // ── Keyword specs ────────────────────────────────────────────────────
        // Each entry: closing keyword → (openers, same-closers)
        // "openers"      = keywords that START the matching block
        // "same-closers" = keywords that END the same block type (used to
        //                  track nesting depth when scanning upward)
        private static readonly Dictionary<string, ClosingSpec> _specs =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENDIF"]   = new(["IF"],                     ["ENDIF"]),
            ["ENDDO"]   = new(["DO WHILE", "DO"],         ["ENDDO"]),
            ["NEXT"]    = new(["FOR", "FOREACH"],         ["NEXT"]),
            ["ELSE"]    = new(["IF", "ELSEIF"],           ["ENDIF"]),
            ["ELSEIF"]  = new(["IF", "ELSEIF"],           ["ENDIF"]),
            ["ENDCASE"] = new(["DO CASE", "SWITCH"],      ["ENDCASE"]),
            ["ENDTRY"]  = new(["TRY"],                    ["ENDTRY"]),
            ["CATCH"]   = new(["TRY"],                    ["ENDTRY"]),
            ["FINALLY"] = new(["TRY"],                    ["ENDTRY"]),
            ["ENDWITH"] = new(["WITH"],                   ["ENDWITH"]),
            // CASE / OTHERWISE always align with DO CASE / SWITCH (not indented inside).
            // Controlled by IndentCase setting: when true the realignment is skipped.
            ["CASE"]      = new(["DO CASE", "SWITCH"],    ["ENDCASE"]),
            ["OTHERWISE"] = new(["DO CASE", "SWITCH"],    ["ENDCASE"]),
        };

        // Function/method header keywords — trigger IndentFunctionBody check.
        private static readonly HashSet<string> _funcHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "FUNCTION", "PROCEDURE", "METHOD", "ACCESS", "ASSIGN",
            "CONSTRUCTOR", "DESTRUCTOR", "OPERATOR", "PROPERTY",
        };

        // Generic block openers/closers for the bare "END" keyword.
        private static readonly string[] GenericOpeners =
        [
            "CLASS", "NAMESPACE", "STRUCTURE", "INTERFACE", "ENUM",
            "BEGIN", "DEFINE",
        ];
        private static readonly string[] GenericClosers =
        [
            "END", "ENDCLASS", "ENDNAMESPACE", "ENDSTRUCTURE", "ENDINTERFACE",
        ];

        public XSharpOnTypeFormattingHandler(
            XSharpDocumentService      documentService,
            XSharpConfigurationService configService,
            ILogger<XSharpOnTypeFormattingHandler> logger)
        {
            _documentService = documentService;
            _configService   = configService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override DocumentOnTypeFormattingRegistrationOptions CreateRegistrationOptions(
            DocumentOnTypeFormattingCapability capability,
            ClientCapabilities clientCapabilities)
            => new DocumentOnTypeFormattingRegistrationOptions
            {
                DocumentSelector      = TextDocumentSelector.ForLanguage("xsharp"),
                FirstTriggerCharacter = "\n",
                MoreTriggerCharacter  = new Container<string>("/"),
            };

        /// <inheritdoc/>
        public override Task<TextEditContainer?> Handle(
            DocumentOnTypeFormattingParams request,
            CancellationToken cancellationToken)
        {
            try
            {
                var uri = request.TextDocument.Uri;
                if (!_documentService.TryGetText(uri, out var text))
                    return Task.FromResult<TextEditContainer?>(null);

                var lines = text.Split('\n');

                if (request.Character == "/")
                    return HandleXmlDocSlash(lines, (int)request.Position.Line);

                // The newline was just inserted, so position.Line is the NEW
                // empty line; the completed line is one above.
                int completedLine = (int)request.Position.Line - 1;
                if (completedLine < 0)
                    return Task.FromResult<TextEditContainer?>(null);

                if (completedLine >= lines.Length)
                    return Task.FromResult<TextEditContainer?>(null);

                string line    = lines[completedLine].TrimEnd('\r');
                string trimmed = line.TrimStart();
                string keyword = FirstKeyword(trimmed);

                if (string.IsNullOrEmpty(keyword))
                    return Task.FromResult<TextEditContainer?>(null);

                // Find the indent the closing keyword should use.
                var settings = _configService.GetSettings();
                string? openerIndent = null;

                if (_specs.TryGetValue(keyword, out var spec))
                {
                    // CASE / OTHERWISE: respect IndentCase setting.
                    // When IndentCase = true the user wants these keywords indented
                    // inside DO CASE/SWITCH, so skip the realignment.
                    bool isCaseBranch = string.Equals(keyword, "CASE",      StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(keyword, "OTHERWISE", StringComparison.OrdinalIgnoreCase);
                    if (isCaseBranch && settings.IndentCase)
                        return Task.FromResult<TextEditContainer?>(null);

                    openerIndent = FindOpenerIndent(lines, completedLine - 1, spec);
                }
                else if (string.Equals(keyword, "END", StringComparison.OrdinalIgnoreCase))
                {
                    openerIndent = FindGenericEndOpenerIndent(lines, completedLine - 1);
                }
                else if (_funcHeaders.Contains(keyword))
                {
                    // Function/method header — handle IndentFunctionBody.
                    return HandleFunctionBodyIndentAsync(lines, completedLine, settings, request.Options);
                }

                if (openerIndent == null)
                    return Task.FromResult<TextEditContainer?>(null);

                // Current leading whitespace of the closing keyword line.
                string currentIndent = line[..(line.Length - trimmed.Length)];
                if (currentIndent == openerIndent)
                    return Task.FromResult<TextEditContainer?>(null); // already aligned

                _logger.LogDebug(
                    "OnTypeFormatting: re-indenting '{Keyword}' at line {Line}",
                    keyword, completedLine);

                var edit = new TextEdit
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(completedLine, 0),
                        new Position(completedLine, currentIndent.Length)),
                    NewText = openerIndent,
                };

                return Task.FromResult<TextEditContainer?>(new TextEditContainer(edit));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnTypeFormatting failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<TextEditContainer?>(null);
            }
        }

        // ====================================================================
        // XML doc comment scaffolding  (triggered by the third '/')
        // ====================================================================

        private static readonly string[] _memberKeywords =
        [
            "FUNCTION", "PROCEDURE", "METHOD", "ACCESS", "ASSIGN",
            "CONSTRUCTOR", "DESTRUCTOR", "PROPERTY",
        ];

        private static readonly string[] _modifiers =
        [
            "PUBLIC", "PROTECTED", "PRIVATE", "INTERNAL", "STATIC", "VIRTUAL",
            "OVERRIDE", "ABSTRACT", "SEALED", "PARTIAL", "HIDDEN", "ASYNC", "UNSAFE", "NEW",
        ];

        private static readonly Regex _asTypeRegex =
            new Regex(@"\bAS\s+([\w:]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private Task<TextEditContainer?> HandleXmlDocSlash(string[] lines, int currentLine)
        {
            if (currentLine >= lines.Length)
                return Task.FromResult<TextEditContainer?>(null);

            string rawLine = lines[currentLine].TrimEnd('\r');
            string trimmed = rawLine.TrimStart();

            // Must be exactly "///" — not a longer comment or a division expression.
            if (!string.Equals(trimmed, "///", StringComparison.Ordinal))
                return Task.FromResult<TextEditContainer?>(null);

            // Skip if the previous line already starts a doc block.
            if (currentLine > 0
                && lines[currentLine - 1].TrimEnd('\r').TrimStart()
                       .StartsWith("///", StringComparison.Ordinal))
                return Task.FromResult<TextEditContainer?>(null);

            // Find the first non-empty line below (the member declaration).
            string? memberLine = null;
            for (int i = currentLine + 1; i < lines.Length; i++)
            {
                string t = lines[i].TrimEnd('\r');
                if (t.Trim().Length > 0) { memberLine = t; break; }
            }

            string indent = rawLine[..(rawLine.Length - trimmed.Length)];
            var (keyword, paramNames, returnType) = ParseMemberSignature(memberLine ?? "");

            // Build the stub.
            var sb = new StringBuilder();
            sb.Append(indent).Append("/// <summary>");
            sb.Append('\n').Append(indent).Append("/// ");
            sb.Append('\n').Append(indent).Append("/// </summary>");

            foreach (var p in paramNames)
                sb.Append('\n').Append(indent).Append($"/// <param name=\"{p}\"></param>");

            // <returns> for FUNCTION / METHOD only (not PROCEDURE / CONSTRUCTOR / DESTRUCTOR / ASSIGN / PROPERTY).
            bool needsReturns = !string.IsNullOrEmpty(returnType)
                && !string.Equals(returnType, "VOID", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(keyword, "FUNCTION",  StringComparison.OrdinalIgnoreCase)
                 || string.Equals(keyword, "METHOD",    StringComparison.OrdinalIgnoreCase)
                 || string.Equals(keyword, "ACCESS",    StringComparison.OrdinalIgnoreCase));

            if (needsReturns)
                sb.Append('\n').Append(indent).Append("/// <returns></returns>");

            _logger.LogDebug("XmlDoc scaffold: keyword={Kw} params={Params} return={Ret}",
                keyword, string.Join(",", paramNames), returnType);

            return Task.FromResult<TextEditContainer?>(new TextEditContainer(new TextEdit
            {
                Range   = new LspRange(new Position(currentLine, 0),
                                       new Position(currentLine, rawLine.Length)),
                NewText = sb.ToString(),
            }));
        }

        /// <summary>
        /// Parses the first member declaration line encountered below the <c>///</c>,
        /// returning the structural keyword, parameter names, and return type (if any).
        /// </summary>
        private static (string keyword, List<string> paramNames, string? returnType)
            ParseMemberSignature(string lineText)
        {
            string remaining = SkipModifiers(lineText.TrimStart());
            string kw = FirstKeyword(remaining);

            bool isMember = false;
            foreach (var mk in _memberKeywords)
                if (string.Equals(mk, kw, StringComparison.OrdinalIgnoreCase)) { isMember = true; break; }

            if (!isMember)
                return (kw, new List<string>(), null);

            // Extract parameter names from (…).
            var paramNames = new List<string>();
            int parenStart = remaining.IndexOf('(');
            int parenEnd   = parenStart >= 0 ? FindClosingParen(remaining, parenStart) : -1;

            if (parenEnd > parenStart)
            {
                foreach (var raw in remaining[(parenStart + 1)..parenEnd].Split(','))
                {
                    string pName = FirstKeyword(raw.Trim());
                    if (!string.IsNullOrEmpty(pName))
                        paramNames.Add(pName);
                }
            }

            // Extract return type: look for "AS <type>" after the closing paren (or member name).
            string searchIn = parenEnd >= 0 ? remaining[(parenEnd + 1)..] : remaining;
            var m = _asTypeRegex.Match(searchIn);
            string? returnType = m.Success ? m.Groups[1].Value : null;

            return (kw, paramNames, returnType);
        }

        private static string SkipModifiers(string text)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var mod in _modifiers)
                {
                    if (text.StartsWith(mod + " ",  StringComparison.OrdinalIgnoreCase)
                     || text.StartsWith(mod + "\t", StringComparison.OrdinalIgnoreCase))
                    {
                        text    = text[mod.Length..].TrimStart();
                        changed = true;
                        break;
                    }
                }
            }
            return text;
        }

        private static int FindClosingParen(string text, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < text.Length; i++)
            {
                if      (text[i] == '(') depth++;
                else if (text[i] == ')') { if (--depth == 0) return i; }
            }
            return -1;
        }

        // ====================================================================
        // Function body indentation
        // ====================================================================

        private Task<TextEditContainer?> HandleFunctionBodyIndentAsync(
            string[] lines,
            int completedLine,
            Models.XSharpWorkspaceSettings settings,
            FormattingOptions options)
        {
            // The new (empty) line is completedLine + 1.
            int newLineIdx = completedLine + 1;
            if (newLineIdx >= lines.Length)
                return Task.FromResult<TextEditContainer?>(null);

            string funcLine    = lines[completedLine].TrimEnd('\r');
            string funcIndent  = funcLine[..(funcLine.Length - funcLine.TrimStart().Length)];
            string indentUnit  = options.InsertSpaces
                ? new string(' ', (int)(options.TabSize > 0 ? options.TabSize : 4))
                : "\t";

            string newLine    = lines[newLineIdx].TrimEnd('\r');
            string newIndent  = newLine[..(newLine.Length - newLine.TrimStart().Length)];

            string targetIndent = settings.IndentFunctionBody
                ? funcIndent + indentUnit   // one level deeper
                : funcIndent;               // same level as the declaration

            if (newIndent == targetIndent)
                return Task.FromResult<TextEditContainer?>(null);

            _logger.LogDebug(
                "OnTypeFormatting: adjusting function body indent at line {L}", newLineIdx);

            return Task.FromResult<TextEditContainer?>(new TextEditContainer(
                new TextEdit
                {
                    Range   = new LspRange(
                                  new Position(newLineIdx, 0),
                                  new Position(newLineIdx, newIndent.Length)),
                    NewText = targetIndent,
                }));
        }

        // ====================================================================
        // Upward scan helpers
        // ====================================================================

        /// <summary>
        /// Scans upward from <paramref name="startLine"/>, tracking nesting
        /// depth, and returns the leading whitespace of the matching opener line.
        /// </summary>
        private static string? FindOpenerIndent(string[] lines, int startLine, ClosingSpec spec)
        {
            int depth = 0;

            for (int i = startLine; i >= 0; i--)
            {
                string trimmed = lines[i].TrimEnd('\r').TrimStart();

                // Same-type closer encountered while scanning upward → deeper nesting.
                foreach (var closer in spec.SameClosers)
                {
                    if (LineStartsWith(trimmed, closer))
                    {
                        depth++;
                        goto nextLine;
                    }
                }

                // Opener encountered.
                foreach (var opener in spec.Openers)
                {
                    if (LineStartsWith(trimmed, opener))
                    {
                        if (depth == 0)
                        {
                            string raw = lines[i].TrimEnd('\r');
                            return raw[..(raw.Length - trimmed.Length)];
                        }
                        depth--;
                        goto nextLine;
                    }
                }

                nextLine:;
            }

            return null;
        }

        /// <summary>
        /// Handles the bare <c>END</c> keyword by scanning upward for any
        /// generic block opener (<c>CLASS</c>, <c>NAMESPACE</c>, <c>BEGIN</c>,
        /// etc.) while correctly tracking depth via generic closers.
        /// </summary>
        private static string? FindGenericEndOpenerIndent(string[] lines, int startLine)
        {
            int depth = 0;

            for (int i = startLine; i >= 0; i--)
            {
                string trimmed = lines[i].TrimEnd('\r').TrimStart();
                string kw      = FirstKeyword(trimmed);

                foreach (var closer in GenericClosers)
                {
                    if (string.Equals(kw, closer, StringComparison.OrdinalIgnoreCase))
                    {
                        depth++;
                        goto nextLine;
                    }
                }

                foreach (var opener in GenericOpeners)
                {
                    if (LineStartsWith(trimmed, opener))
                    {
                        if (depth == 0)
                        {
                            string raw = lines[i].TrimEnd('\r');
                            return raw[..(raw.Length - trimmed.Length)];
                        }
                        depth--;
                        goto nextLine;
                    }
                }

                nextLine:;
            }

            return null;
        }

        // ====================================================================
        // Utilities
        // ====================================================================

        /// <summary>
        /// Returns the first contiguous identifier word from
        /// <paramref name="trimmedLine"/> (letters, digits, underscore only).
        /// </summary>
        private static string FirstKeyword(string trimmedLine)
        {
            int i = 0;
            while (i < trimmedLine.Length
                   && (char.IsLetterOrDigit(trimmedLine[i]) || trimmedLine[i] == '_'))
                i++;
            return trimmedLine[..i];
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="trimmedLine"/> starts with
        /// <paramref name="keyword"/> (case-insensitive) followed by whitespace,
        /// end-of-string, or a non-identifier character.  Supports multi-word
        /// keywords such as <c>"DO CASE"</c> and <c>"DO WHILE"</c>.
        /// </summary>
        private static bool LineStartsWith(string trimmedLine, string keyword)
        {
            if (!trimmedLine.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return false;

            if (trimmedLine.Length == keyword.Length) return true;

            char next = trimmedLine[keyword.Length];
            return !char.IsLetterOrDigit(next) && next != '_';
        }

        // ====================================================================
        // Inner types
        // ====================================================================

        private sealed record ClosingSpec(string[] Openers, string[] SameClosers);
    }
}
