using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using XSharpLanguageServer.Handlers;
using XSharpLanguageServer.Models;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Performs a lightweight semantic analysis pass over a parsed document and
    /// returns additional <see cref="Diagnostic"/> objects beyond what the XSharp
    /// parser itself reports.
    /// <para>
    /// Only activated when <c>xsharp.semanticDiagnostics</c> is <c>true</c> in
    /// the workspace settings (off by default to avoid false positives).
    /// </para>
    /// <para>
    /// Current checks:
    /// <list type="bullet">
    ///   <item>
    ///     <b>XS0001 — Too many arguments</b>: a function or method call passes
    ///     more arguments than any of its declared overloads accept.  Only
    ///     triggered when at least one overload is found in the workspace index
    ///     or the assembly DB, and no overload uses <c>PARAMS</c> (variadic).
    ///     Reported as a <see cref="DiagnosticSeverity.Warning"/> because full
    ///     type resolution is unavailable.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class XSharpSemanticDiagnosticsService
    {
        private readonly XSharpWorkspaceIndex    _index;
        private readonly XSharpDatabaseService   _db;
        private readonly XSharpConfigurationService _configService;
        private readonly ILogger<XSharpSemanticDiagnosticsService> _logger;

        // Extracts the parameter list content between the first ( and matching ).
        private static readonly Regex _paramBlock = new(
            @"\(([^)]*)\)", RegexOptions.Compiled);

        // XSharp built-in type keywords — never flagged as unknown types.
        private static readonly HashSet<string> _builtInTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "VOID", "OBJECT", "STRING", "INT", "INT32", "DWORD", "UINT32",
            "WORD", "UINT16", "BYTE", "UINT8", "SHORTINT", "INT16",
            "INT64", "UINT64", "REAL4", "SINGLE", "REAL8", "DOUBLE",
            "LOGIC", "BOOL", "BOOLEAN", "CHAR", "ARRAY", "USUAL",
            "DATE", "SYMBOL", "PSZ", "PTR", "DYNAMIC", "VAR",
            "LONG", "ULONG", "SHORT", "USHORT", "DECIMAL", "CURRENCY",
            "FLOAT", "INT8", "UINT8",
            // Common .NET aliases used without USING
            "EXCEPTION", "OBJECT",
        };

        public XSharpSemanticDiagnosticsService(
            XSharpWorkspaceIndex        index,
            XSharpDatabaseService       db,
            XSharpConfigurationService  configService,
            ILogger<XSharpSemanticDiagnosticsService> logger)
        {
            _index         = index;
            _db            = db;
            _configService = configService;
            _logger        = logger;
        }

        /// <summary>
        /// Analyses the parse tree and returns semantic diagnostics.
        /// Safe to call on any thread; all index/DB access is read-only.
        /// </summary>
        public IReadOnlyList<Diagnostic> Analyze(
            XSharpParserRuleContext tree,
            string filePath)
        {
            var results = new List<Diagnostic>();
            try
            {
                WalkForCalls(tree, filePath, results);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SemanticDiagnostics: analysis failed for {File}", filePath);
            }
            return results;
        }

        // ====================================================================
        // Tree walker
        // ====================================================================

        private void WalkForCalls(IParseTree node, string filePath, List<Diagnostic> results)
        {
            var settings = _configService.GetSettings();

            if (node is XSharpParser.MethodCallContext mc)
            {
                CheckArgumentCount(mc, results);

                if (settings.WarnOnUndefinedCalls)
                    CheckUndefinedCall(mc, results);
            }

            if (node is XSharpParser.CommonLocalDeclContext decl)
                CheckUnknownLocalTypes(decl, results);

            for (int i = 0; i < node.ChildCount; i++)
                WalkForCalls(node.GetChild(i), filePath, results);
        }

        // ====================================================================
        // XS0001 — Wrong argument count
        // ====================================================================

        private void CheckArgumentCount(
            XSharpParser.MethodCallContext mc,
            List<Diagnostic> results)
        {
            // Extract the callee name.
            var calleeText = mc.expression()?.GetText();
            if (string.IsNullOrEmpty(calleeText)) return;

            string name = SimpleName(calleeText);
            if (string.IsNullOrEmpty(name)) return;

            // Count the actual arguments supplied.
            var argList  = mc.argumentList();
            int argCount = argList?.namedArgument()?.Length ?? 0;

            // Look up all overloads — workspace index first, DB fallback.
            var overloads = _index.FindOverloads(name);
            if (overloads.Count == 0 && _db.IsAvailable)
                overloads = _db.FindAssemblyOverloads(name);

            if (overloads.Count == 0) return;   // unknown callee — skip

            // Evaluate each overload.
            int maxParams = 0;
            bool anyAccepts = false;

            foreach (var sym in overloads)
            {
                // A PARAMS overload accepts any number of arguments — skip check.
                if (sym.Sourcecode?.Contains("PARAMS", StringComparison.OrdinalIgnoreCase) == true)
                    return;

                int paramCount = CountParams(sym.Sourcecode);
                maxParams = Math.Max(maxParams, paramCount);

                // Count optional parameters (those with DEFAULT) to determine minimum.
                int optCount = CountOptionalParams(sym.Sourcecode);
                int minCount = paramCount - optCount;

                if (argCount >= minCount && argCount <= paramCount)
                {
                    anyAccepts = true;
                    break;
                }
            }

            if (anyAccepts) return;

            if (argCount > maxParams)
            {
                int line   = Math.Max(0, mc.Start.Line - 1);
                int col    = Math.Max(0, mc.Start.Column);
                int endCol = col + name.Length;

                results.Add(new Diagnostic
                {
                    Range    = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                   new Position(line, col), new Position(line, endCol)),
                    Severity = DiagnosticSeverity.Warning,
                    Code     = new DiagnosticCode("XS0001"),
                    Source   = "xsharp-semantic",
                    Message  = $"Too many arguments: '{name}' accepts at most {maxParams} argument(s), got {argCount}.",
                });

                _logger.LogDebug(
                    "SemanticDiagnostics XS0001: {Name} — expected ≤{Max}, got {Got}",
                    name, maxParams, argCount);
            }
        }

        // ====================================================================
        // XS0002 — Undefined function call
        // ====================================================================

        private void CheckUndefinedCall(
            XSharpParser.MethodCallContext mc,
            List<Diagnostic> results)
        {
            string name = SimpleName(mc.Expr?.GetText() ?? string.Empty);
            if (string.IsNullOrEmpty(name)) return;

            // Known in workspace index or assembly DB → not undefined.
            if (_index.FindExact(name) != null) return;
            if (_db.IsAvailable && _db.FindAssemblyExact(name) != null) return;

            int line   = Math.Max(0, mc.Start.Line - 1);
            int col    = Math.Max(0, mc.Start.Column);
            int endCol = col + name.Length;

            results.Add(new Diagnostic
            {
                Range    = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                               new Position(line, col), new Position(line, endCol)),
                Severity = DiagnosticSeverity.Information,
                Code     = new DiagnosticCode("XS0002"),
                Source   = "xsharp-semantic",
                Message  = $"'{name}' is not defined in the workspace index or referenced assemblies.",
            });
        }

        // ====================================================================
        // XS0003 — Unknown type in LOCAL declaration
        // ====================================================================

        private void CheckUnknownLocalTypes(
            XSharpParser.CommonLocalDeclContext decl,
            List<Diagnostic> results)
        {
            if (decl._LocalVars == null) return;

            foreach (var lv in decl._LocalVars)
            {
                string? rawType = lv.DataType?.GetText();
                if (string.IsNullOrEmpty(rawType)) continue;

                // Strip generics and array suffixes.
                string typeName = rawType;
                int lt = typeName.IndexOf('<');
                if (lt > 0) typeName = typeName[..lt];
                typeName = typeName.TrimEnd('?', '[', ']', ' ').Trim();

                // Take the last segment of a qualified name.
                int dot = typeName.LastIndexOf('.');
                if (dot >= 0) typeName = typeName[(dot + 1)..];

                if (string.IsNullOrEmpty(typeName)) continue;
                if (_builtInTypes.Contains(typeName)) continue;

                // Check workspace index and assembly DB.
                if (_index.FindExact(typeName) != null) continue;
                if (_db.IsAvailable && _db.FindAssemblyExact(typeName) != null) continue;

                int line   = Math.Max(0, (lv.DataType?.Start?.Line ?? 1) - 1);
                int col    = Math.Max(0, lv.DataType?.Start?.Column ?? 0);
                int endCol = col + rawType.Length;

                results.Add(new Diagnostic
                {
                    Range    = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                   new Position(line, col), new Position(line, endCol)),
                    Severity = DiagnosticSeverity.Warning,
                    Code     = new DiagnosticCode("XS0003"),
                    Source   = "xsharp-semantic",
                    Message  = $"Type '{typeName}' is not defined in the workspace index or referenced assemblies.",
                });
            }
        }

        // ====================================================================
        // Parameter counting helpers
        // ====================================================================

        /// <summary>
        /// Counts the number of declared parameters in a prototype string such as
        /// <c>FUNCTION Foo(x AS INT, y AS STRING) AS VOID</c>.
        /// Returns 0 if the prototype is empty or has no parentheses.
        /// </summary>
        private static int CountParams(string? sourcecode)
            => XSharpInlayHintsHandler.ParseParamNames(sourcecode).Count;

        /// <summary>
        /// Counts parameters that carry a <c>DEFAULT</c> keyword in the prototype —
        /// these are optional and need not be supplied by the caller.
        /// </summary>
        private static int CountOptionalParams(string? sourcecode)
        {
            if (string.IsNullOrEmpty(sourcecode)) return 0;

            var m = _paramBlock.Match(sourcecode);
            if (!m.Success) return 0;

            string paramsPart = m.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(paramsPart)) return 0;

            // Split on commas (naively — good enough for single-line prototypes).
            int count = 0;
            foreach (var part in paramsPart.Split(','))
            {
                if (part.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        private static string SimpleName(string expr)
        {
            int colon = expr.LastIndexOf(':');
            int dot   = expr.LastIndexOf('.');
            int sep   = Math.Max(colon, dot);
            return sep >= 0 ? expr[(sep + 1)..] : expr;
        }
    }
}
