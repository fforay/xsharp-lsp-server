using System.Collections.Generic;

namespace XSharpLanguageServer.Models
{
    /// <summary>
    /// Mirrors the <c>xsharp</c> section of the client's workspace configuration.
    /// All properties are optional; defaults reproduce the behaviour of
    /// <see cref="LanguageService.CodeAnalysis.XSharp.XSharpParseOptions.Default"/>.
    /// </summary>
    public sealed class XSharpWorkspaceSettings
    {
        /// <summary>
        /// XSharp dialect to use when parsing.
        /// Accepted values (case-insensitive): Core, VO, Vulcan, Harbour, FoxPro, XPP, dBase.
        /// Defaults to <c>"Core"</c>.
        /// </summary>
        public string Dialect { get; set; } = "Core";

        /// <summary>
        /// Semicolon-separated list of additional directories to search for
        /// <c>#include</c> files, e.g. <c>"C:\\MyApp\\Include;C:\\XSharp\\Include"</c>.
        /// Defaults to empty (no extra paths).
        /// </summary>
        public string IncludePaths { get; set; } = "";

        /// <summary>
        /// Path to a header file that is automatically included before every source
        /// file, equivalent to the <c>&lt;StandardDefs&gt;</c> property in an
        /// <c>.xsproj</c> and the <c>/stddefs:</c> compiler switch.
        /// When empty, no extra header is injected.
        /// </summary>
        public string StandardDefs { get; set; } = "";

        /// <summary>
        /// Extra preprocessor symbols to define, separated by semicolons,
        /// e.g. <c>"DEBUG;MYFLAG"</c>.
        /// Defaults to empty.
        /// </summary>
        public string PreprocessorSymbols { get; set; } = "";

        /// <summary>
        /// When <c>true</c>, the server runs an additional semantic analysis pass
        /// after each parse and publishes extra diagnostics (e.g. wrong argument
        /// count).  Off by default because some checks may produce false positives
        /// without full type resolution.
        /// Controlled by <c>xsharp.semanticDiagnostics</c> in VS Code settings.
        /// </summary>
        public bool SemanticDiagnostics { get; set; } = false;

        /// <summary>
        /// When <c>true</c> (and <see cref="SemanticDiagnostics"/> is also <c>true</c>),
        /// the server emits an <c>XS0002</c> <see cref="DiagnosticSeverity.Information"/>
        /// diagnostic for calls to functions not found in the workspace index or the
        /// assembly database.  Off by default due to high false-positive risk from
        /// FoxPro runtime functions and other external globals.
        /// Controlled by <c>xsharp.warnOnUndefinedCalls</c> in VS Code settings.
        /// </summary>
        public bool WarnOnUndefinedCalls { get; set; } = false;

        /// <summary>
        /// When <c>false</c> (default), <c>CASE</c> and <c>OTHERWISE</c> are
        /// auto-aligned to the same indentation as their <c>DO CASE</c> / <c>SWITCH</c>
        /// opener on Enter.  When <c>true</c>, they are left indented one level inside.
        /// Controlled by <c>xsharp.indentCase</c>.
        /// </summary>
        // ── Indentation settings (mirrors Visual Studio IndentingOptionsPage) ──

        /// <summary>Align CASE/OTHERWISE with DO CASE/SWITCH when false (default).</summary>
        public bool IndentCaseLabel    { get; set; } = false;
        /// <summary>Indent statements inside each CASE/OTHERWISE branch.</summary>
        public bool IndentCaseContent  { get; set; } = true;
        /// <summary>Indent statements inside FUNCTION/METHOD/PROCEDURE bodies.</summary>
        public bool IndentBlockContent { get; set; } = true;
        /// <summary>Indent multiline members inside CLASS/STRUCTURE.</summary>
        public bool IndentEntityContent { get; set; } = true;
        /// <summary>Indent single-line fields/properties inside CLASS/STRUCTURE.</summary>
        public bool IndentFieldContent  { get; set; } = true;
        /// <summary>Indent entities declared inside a NAMESPACE block.</summary>
        public bool IndentNamespace     { get; set; } = false;
        /// <summary>Indent continuation lines in multi-line statements.</summary>
        public bool IndentMultiLines    { get; set; } = true;
        /// <summary>Indent preprocessor directives with surrounding code.</summary>
        public bool IndentPreprocessorLines { get; set; } = false;

        // ── Hover settings ───────────────────────────────────────────────────
        /// <summary>
        /// When <c>true</c> (default), hovering over a built-in keyword (IF, RETURN,
        /// CLASS, …) shows a one-line description tooltip.
        /// Set to <c>false</c> to suppress keyword hover entirely.
        /// Controlled by <c>xsharp.hoverKeywords</c> in VS Code settings.
        /// </summary>
        public bool HoverKeywords { get; set; } = true;

        // ── Formatting settings ───────────────────────────────────────────────
        /// <summary>Keyword case: None, Upper (default), Lower, Title.</summary>
        public string KeywordCase          { get; set; } = "Upper";
        /// <summary>Remove trailing whitespace when formatting.</summary>
        public bool   TrimTrailingWhitespace { get; set; } = true;
        /// <summary>Insert a final newline at end of file when formatting.</summary>
        public bool   InsertFinalNewline   { get; set; } = false;

        // ── Legacy aliases kept for backward compatibility ────────────────────
        /// <summary>Alias for <see cref="IndentCaseLabel"/>.</summary>
        public bool IndentCase         { get => IndentCaseLabel;    set => IndentCaseLabel    = value; }
        /// <summary>Alias for <see cref="IndentBlockContent"/>.</summary>
        public bool IndentFunctionBody { get => IndentBlockContent; set => IndentBlockContent = value; }
    }
}
