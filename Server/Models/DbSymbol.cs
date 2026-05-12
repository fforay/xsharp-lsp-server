namespace XSharpLanguageServer.Models
{
    /// <summary>A symbol (type or member) returned by a lookup or prefix query.</summary>
    public sealed class DbSymbol
    {
        /// <summary>Declared name of the symbol.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Kind integer from the database.
        /// Matches the XSharp <c>Kind</c> enum used by the code model
        /// (e.g. Class=1, Method=2, Function=5, …).
        /// </summary>
        public int Kind { get; init; }

        /// <summary>Return type name, or <c>null</c> for types.</summary>
        public string? ReturnType { get; init; }

        /// <summary>
        /// Source prototype string — e.g. <c>METHOD Foo(x AS INT) AS STRING</c>.
        /// Used for hover tooltips and signature help.
        /// </summary>
        public string? Sourcecode { get; init; }

        /// <summary>XML doc comment text attached to the declaration, if any.</summary>
        public string? XmlComments { get; init; }

        /// <summary>Absolute path of the file that contains this declaration.</summary>
        public string? FileName { get; init; }

        /// <summary>0-based start line (already converted from the DB's 1-based value).</summary>
        public int StartLine { get; init; }

        /// <summary>0-based start column.</summary>
        public int StartCol { get; init; }
    }
}
