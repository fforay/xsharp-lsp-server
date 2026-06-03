namespace XSharpLanguageServer.Models
{
    /// <summary>
    /// Integer kind constants shared between <see cref="WorkspaceSymbol"/> and
    /// <see cref="DbSymbol"/>.
    /// <para>
    /// Values 1–11 match the integers stored in the XSharp IntelliSense database
    /// (<c>X#Model.xsdb</c>) so that workspace-index lookups and DB-fallback lookups
    /// can be processed by the same handler code.  Values 12+ cover declaration
    /// types that appear in source but are not represented in the DB schema.
    /// </para>
    /// </summary>
    public static class XSharpSymbolKind
    {
        // ── DB-matched values (must stay stable) ────────────────────────────
        public const int Class       = 1;
        public const int Method      = 2;
        public const int Access      = 3;   // VO-style ACCESS (property getter)
        public const int Assign      = 3;   // VO-style ASSIGN (property setter) — same slot as Access
        public const int Field       = 4;   // instance variable / iVar
        public const int Function    = 5;
        public const int Procedure   = 6;
        public const int Global      = 7;   // module-level GLOBAL variable
        public const int Interface   = 8;
        public const int Structure   = 9;
        public const int Enum        = 10;
        public const int EnumMember  = 11;

        // ── Source-only values ───────────────────────────────────────────────
        public const int Define      = 12;  // DEFINE constant
        public const int Namespace   = 13;
        public const int Constructor = 14;
        public const int Destructor  = 15;
        public const int Event       = 16;
        public const int Delegate    = 17;
        public const int Property    = 18;  // modern C#-style PROPERTY (not ACCESS/ASSIGN)
    }
}
