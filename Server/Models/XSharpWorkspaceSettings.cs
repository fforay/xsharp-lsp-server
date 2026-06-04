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
    }
}
