using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using XSharpLanguageServer.Handlers;

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
        private readonly XSharpWorkspaceIndex   _index;
        private readonly XSharpDatabaseService  _db;
        private readonly ILogger<XSharpSemanticDiagnosticsService> _logger;

        // Extracts the parameter list content between the first ( and matching ).
        private static readonly Regex _paramBlock = new(
            @"\(([^)]*)\)", RegexOptions.Compiled);

        public XSharpSemanticDiagnosticsService(
            XSharpWorkspaceIndex  index,
            XSharpDatabaseService db,
            ILogger<XSharpSemanticDiagnosticsService> logger)
        {
            _index  = index;
            _db     = db;
            _logger = logger;
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
            if (node is XSharpParser.MethodCallContext mc)
            {
                CheckArgumentCount(mc, results);
                // still recurse — calls can be nested
            }

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
