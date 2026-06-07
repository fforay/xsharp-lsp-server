namespace XSharpLanguageServer.Models
{
    /// <summary>A symbol extracted from source files by the workspace index.</summary>
    public sealed class WorkspaceSymbol
    {
        /// <summary>Declared name of the symbol.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Kind integer matching the XSharp code-model Kind enum
        /// (e.g. Class=1, Method=2, Function=5, …).
        /// </summary>
        public int Kind { get; init; }

        /// <summary>Return type name, or <c>null</c> for type declarations.</summary>
        public string? ReturnType { get; init; }

        /// <summary>
        /// Source prototype string — e.g. <c>METHOD Foo(x AS INT) AS STRING</c>.
        /// Used for hover tooltips and signature help.
        /// </summary>
        public string? Sourcecode { get; init; }

        /// <summary>XML doc comment text attached to the declaration, if any.</summary>
        public string? XmlComments { get; init; }

        /// <summary>Absolute path of the file that contains this declaration.</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>0-based start line (LSP convention).</summary>
        public int StartLine { get; init; }

        /// <summary>0-based start column.</summary>
        public int StartCol { get; init; }

        /// <summary>
        /// Name of the declaring type, or <c>null</c> for top-level types and global functions.
        /// </summary>
        public string? TypeName { get; init; }

        /// <summary>
        /// Namespace the symbol is declared in (from a <c>BEGIN NAMESPACE</c> block),
        /// or <c>null</c> when declared at file scope.
        /// Used by the <em>Add USING</em> code action to insert the correct directive.
        /// </summary>
        public string? Namespace { get; init; }

        /// <summary>
        /// Name of the parent class from the <c>INHERIT</c> clause, or <c>null</c>
        /// when not a class declaration or no parent is specified.
        /// Used by <see cref="XSharpLanguageServer.Services.XSharpWorkspaceIndex.GetMembersOf"/>
        /// to walk the inheritance chain for member completion.
        /// </summary>
        public string? InheritsFrom { get; init; }
    }
}
