namespace XSharpLanguageServer.Models
{
    /// <summary>
    /// A single occurrence of an identifier token in a source file.
    /// Stored in <c>XSharpWorkspaceIndex</c> to support full-project
    /// <c>textDocument/references</c> across closed files.
    /// </summary>
    public readonly struct IdentifierLocation
    {
        /// <summary>The identifier text as it appears in the source.</summary>
        public string Text { get; init; }

        /// <summary>Absolute path of the file containing this token.</summary>
        public string FilePath { get; init; }

        /// <summary>0-based line number (LSP convention).</summary>
        public int Line { get; init; }

        /// <summary>0-based column of the token start.</summary>
        public int Col { get; init; }
    }
}
