using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using XSharpLanguageServer.Services;
using XSharpLanguageServer.Models;
namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>textDocument/hover</c> LSP request.
    /// <para>
    /// When the cursor rests on an identifier, this handler:
    /// <list type="number">
    ///   <item>Extracts the word under the cursor from the current document text.</item>
    ///   <item>Checks the static keyword dictionary — if the word is a known XSharp
    ///         keyword a tooltip is returned immediately without hitting the DB.</item>
    ///   <item>Looks it up in the XSharp IntelliSense database (<c>X#Model.xsdb</c>)
    ///         via <see cref="XSharpDatabaseService.FindExact"/>.</item>
    ///   <item>Returns a Markdown hover card containing the prototype
    ///         (<c>Sourcecode</c>) and optional XML doc comment.</item>
    /// </list>
    /// Returns <c>null</c> (no hover) when neither the keyword dictionary nor the
    /// database contains a match.
    /// </para>
    /// </summary>
    public class XSharpHoverHandler : HoverHandlerBase
    {
        // ====================================================================
        // Static keyword tooltip dictionary
        // Key   = canonical UPPERCASE keyword (case-insensitive lookup)
        // Value = short description shown in the hover card
        // ====================================================================
        private static readonly System.Collections.Generic.Dictionary<string, string>
            _keywordDocs = new(StringComparer.OrdinalIgnoreCase)
        {
            // --- Declaration keywords ---
            { "FUNCTION",    "Declares a global function that returns a value." },
            { "PROCEDURE",   "Declares a global procedure (no return value)." },
            { "CLASS",       "Declares a reference type (object-oriented class)." },
            { "INTERFACE",   "Declares a contract that classes can implement." },
            { "STRUCTURE",   "Declares a value type (stack-allocated struct)." },
            { "ENUM",        "Declares an enumeration of named integer constants." },
            { "DELEGATE",    "Declares a type-safe function pointer." },
            { "METHOD",      "Declares a method inside a CLASS or STRUCTURE." },
            { "CONSTRUCTOR", "Defines the initialisation code for a class instance." },
            { "DESTRUCTOR",  "Defines code that runs when an object is garbage-collected." },
            { "PROPERTY",    "Declares a class property with optional GET/SET accessors." },
            { "ACCESS",      "Declares the getter part of a VO-style property." },
            { "ASSIGN",      "Declares the setter part of a VO-style property." },
            { "EVENT",       "Declares a multicast delegate event on a class." },
            { "OPERATOR",    "Defines an overloaded operator for a class or structure." },
            { "NAMESPACE",   "Groups types under a hierarchical name." },
            { "GLOBAL",      "Declares a module-level variable visible across the file." },
            { "DEFINE",      "Declares a compile-time constant." },
            { "FIELD",       "Declares an instance variable in a VO-style CLASS." },
            { "MEMBER",      "Declares a member variable (alias for FIELD in some dialects)." },

            // --- Control flow ---
            { "IF",          "Conditionally executes a block of code." },
            { "ELSEIF",      "Tests an additional condition when the preceding IF/ELSEIF was false." },
            { "ELSE",        "Executes a block when no preceding IF/ELSEIF condition was true." },
            { "ENDIF",       "Closes an IF / ELSEIF / ELSE block." },
            { "DO",          "Begins a DO WHILE or DO CASE construct." },
            { "WHILE",       "Repeats a block as long as a condition is true (DO WHILE … ENDDO)." },
            { "ENDDO",       "Closes a DO WHILE loop." },
            { "FOR",         "Counter-controlled loop: FOR var := start TO end [STEP n] … NEXT." },
            { "FOREACH",     "Iterates over every element in a collection: FOREACH var IN collection … NEXT." },
            { "NEXT",        "Closes a FOR or FOREACH loop." },
            { "REPEAT",      "Begins a REPEAT … UNTIL post-condition loop." },
            { "UNTIL",       "Closes a REPEAT loop; loop runs until the condition is true." },
            { "EXIT",        "Immediately exits the enclosing loop." },
            { "LOOP",        "Skips to the next iteration of the enclosing loop." },
            { "BREAK",       "Raises a run-time error or exits a BEGIN SEQUENCE block." },
            { "RETURN",      "Returns from the current function/method, optionally with a value." },
            { "CASE",        "Introduces a branch inside a DO CASE or SWITCH construct." },
            { "OTHERWISE",   "Default branch executed when no CASE matches." },
            { "ENDCASE",     "Closes a DO CASE construct." },
            { "SWITCH",      "Evaluates an expression and branches to a matching CASE." },

            // --- Exception handling ---
            { "TRY",         "Begins a structured exception-handling block." },
            { "CATCH",       "Handles an exception thrown inside a TRY block." },
            { "FINALLY",     "Code that always runs after a TRY/CATCH, whether or not an exception occurred." },
            { "ENDTRY",      "Closes a TRY / CATCH / FINALLY block." },
            { "THROW",       "Raises an exception." },

            // --- BEGIN constructs ---
            { "BEGIN",       "Starts a BEGIN NAMESPACE, BEGIN SEQUENCE, BEGIN LOCK, or BEGIN USING block." },
            { "END",         "Closes a BEGIN block (BEGIN NAMESPACE, BEGIN SEQUENCE, etc.)." },
            { "SEQUENCE",    "Used with BEGIN SEQUENCE … END SEQUENCE for VO-style error recovery." },

            // --- Object / type system ---
            { "NEW",         "Allocates and initialises a new object instance." },
            { "SELF",        "Refers to the current object instance inside a method." },
            { "SUPER",       "Calls the parent class constructor or method." },
            { "TYPEOF",      "Returns the System.Type object for a type at compile time." },
            { "IS",          "Tests whether an object is an instance of a given type." },
            { "AS",          "Casts an expression to a type (returns NULL on failure when used as AS operator)." },
            { "CAST",        "Performs an explicit type cast (raises an exception on failure)." },
            { "INHERIT",     "Specifies the base class for a CLASS declaration." },
            { "IMPLEMENTS",  "Specifies interfaces a CLASS implements." },
            { "ABSTRACT",    "Modifier: the class or method has no implementation and must be overridden." },
            { "VIRTUAL",     "Modifier: the method can be overridden in a derived class." },
            { "OVERRIDE",    "Modifier: this method replaces a VIRTUAL method from the base class." },
            { "SEALED",      "Modifier: prevents a class from being inherited or a method from being overridden." },
            { "STATIC",      "Modifier: the member belongs to the type, not to an instance." },
            { "PARTIAL",     "Modifier: allows a class or method body to be split across multiple files." },
            { "EXTERN",      "Modifier: the method body is defined outside the managed assembly (P/Invoke)." },
            { "UNSAFE",      "Modifier: allows pointer arithmetic inside the block." },

            // --- Access modifiers ---
            { "PUBLIC",      "Access modifier: visible everywhere." },
            { "PROTECTED",   "Access modifier: visible within the class and derived classes." },
            { "PRIVATE",     "Access modifier: visible only within the declaring class." },
            { "INTERNAL",    "Access modifier: visible within the same assembly." },
            { "HIDDEN",      "VO-style access modifier equivalent to PRIVATE." },
            { "EXPORT",      "VO-style access modifier equivalent to PUBLIC." },

            // --- Type keywords ---
            { "VOID",        "Indicates a function or method returns no value." },
            { "OBJECT",      "The root type of the XSharp / .NET type hierarchy." },
            { "STRING",      "Immutable sequence of Unicode characters." },
            { "INT",         "32-bit signed integer (alias for System.Int32)." },
            { "DWORD",       "32-bit unsigned integer (alias for System.UInt32)." },
            { "WORD",        "16-bit unsigned integer (alias for System.UInt16)." },
            { "BYTE",        "8-bit unsigned integer (alias for System.Byte)." },
            { "SHORTINT",    "16-bit signed integer (alias for System.Int16)." },
            { "INT64",       "64-bit signed integer (alias for System.Int64)." },
            { "UINT64",      "64-bit unsigned integer (alias for System.UInt64)." },
            { "REAL4",       "32-bit IEEE floating-point number (alias for System.Single)." },
            { "REAL8",       "64-bit IEEE floating-point number (alias for System.Double)." },
            { "LOGIC",       "Boolean type (TRUE / FALSE)." },
            { "CHAR",        "A single Unicode character (alias for System.Char)." },
            { "ARRAY",       "VO-style dynamic array (1-based, variant element type)." },
            { "USUAL",       "VO/Vulcan variant type that can hold any value." },
            { "DATE",        "VO-style date type." },
            { "SYMBOL",      "VO-style interned symbol (compile-time string constant)." },
            { "PSZ",         "VO-style pointer to a null-terminated ANSI string." },
            { "PTR",         "VO-style untyped pointer." },
            { "VAR",         "Implicitly-typed local variable (type inferred from the initialiser)." },
            { "DYNAMIC",     "Dynamically typed variable; member resolution deferred to runtime." },

            // --- Miscellaneous ---
            { "LOCAL",       "Declares a local variable inside a function or method." },
            { "MEMVAR",      "Declares a memory variable (VO/Clipper dynamic variable)." },
            { "PARAMETERS",  "Declares Clipper-style positional parameters." },
            { "IN",          "Used in FOREACH … IN and membership-test expressions." },
            { "OUT",         "Parameter modifier: the argument is passed by reference and assigned before return." },
            { "REF",         "Parameter modifier: the argument is passed by reference." },
            { "PARAMS",      "Parameter modifier: captures a variable number of arguments as an array." },
            { "DEFAULT",     "Sets a default value for an optional parameter." },
            { "IMPLIED",     "Used with LOCAL IMPLIED or VAR to infer the type from the initialiser." },
            { "IIF",         "Inline IF: IIF(condition, trueValue, falseValue)." },
            { "NIL",         "The null/empty USUAL value in VO/Vulcan dialects." },
            { "NULL",        "The null reference value." },
            { "NULL_ARRAY",  "A null ARRAY reference." },
            { "NULL_DATE",   "A null DATE value." },
            { "NULL_OBJECT", "A null OBJECT reference." },
            { "NULL_PSZ",    "A null PSZ pointer." },
            { "NULL_STRING", "A null STRING reference." },
            { "NULL_SYMBOL", "A null SYMBOL value." },
            { "TRUE",        "Boolean literal: logical true." },
            { "FALSE",       "Boolean literal: logical false." },
            { "WITH",        "Begins a WITH … END WITH block to avoid repeating an object expression." },
            { "ENDWITH",     "Closes a WITH block." },
            { "USING",       "Imports a namespace (USING System.IO) or ensures disposal (BEGIN USING)." },
            { "LOCK",        "BEGIN LOCK obj … END LOCK: acquires a monitor lock for thread safety." },
            { "CHECKED",     "Enables overflow checking for arithmetic operations in the block." },
            { "UNCHECKED",   "Disables overflow checking for arithmetic operations in the block." },
            { "FIXED",       "Pins a managed object in memory for unmanaged pointer access." },
            { "SIZEOF",      "Returns the size in bytes of a value type at compile time." },
            { "TYPEOF",      "Returns the System.Type descriptor for a type." },
            { "NAMEOF",      "Returns the name of a variable, type, or member as a string." },
            { "VOLATILE",    "Marks a field as volatile (reads/writes not cached by the CPU)." },
        };
        private readonly XSharpDocumentService    _documentService;
        private readonly XSharpDatabaseService    _dbService;
        private readonly ILogger<XSharpHoverHandler> _logger;

        /// <summary>Initialises the handler. Called by the DI container.</summary>
        public XSharpHoverHandler(
            XSharpDocumentService       documentService,
            XSharpDatabaseService       dbService,
            ILogger<XSharpHoverHandler> logger)
        {
            _documentService = documentService;
            _dbService       = dbService;
            _logger          = logger;
        }

        /// <inheritdoc/>
        protected override HoverRegistrationOptions CreateRegistrationOptions(
            HoverCapability capability,
            ClientCapabilities clientCapabilities)
            => new HoverRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
            };

        /// <inheritdoc/>
        public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
        {
            try
            {
                var uri  = request.TextDocument.Uri;
                var pos  = request.Position;

                var text = _documentService.TryGetText(uri, out var txt) ? txt : null;
                if (text == null)
                    return Task.FromResult<Hover?>(null);

                string word = ExtractWord(text, pos);
                if (string.IsNullOrEmpty(word))
                    return Task.FromResult<Hover?>(null);

                // ----------------------------------------------------------
                // 1. Keyword dictionary — no DB required.
                // ----------------------------------------------------------
                if (_keywordDocs.TryGetValue(word, out string? kwDesc))
                {
                    var hover = new Hover
                    {
                        Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                        {
                            Kind  = MarkupKind.Markdown,
                            Value = $"```xsharp\n{word.ToUpperInvariant()}\n```\n\n{kwDesc}",
                        }),
                    };
                    return Task.FromResult<Hover?>(hover);
                }

                // ----------------------------------------------------------
                // 2. IntelliSense database — user-defined symbols.
                // ----------------------------------------------------------
                if (!_dbService.IsAvailable)
                    return Task.FromResult<Hover?>(null);

                string? filePath = uri.GetFileSystemPath();
                var symbol = _dbService.FindExact(word, filePath);
                if (symbol == null)
                    return Task.FromResult<Hover?>(null);

                var md = BuildMarkdown(symbol);
                var dbHover = new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind  = MarkupKind.Markdown,
                        Value = md,
                    }),
                };

                return Task.FromResult<Hover?>(dbHover);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hover failed for {Uri}", request.TextDocument.Uri);
                return Task.FromResult<Hover?>(null);
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        /// <summary>
        /// Extracts the word (XSharp identifier) under <paramref name="pos"/>
        /// from <paramref name="text"/>.
        /// </summary>
        private static string ExtractWord(string text, Position pos)
        {
            var lines = text.Split('\n');
            if (pos.Line >= lines.Length) return string.Empty;

            string line   = lines[pos.Line];
            int    col    = Math.Min((int)pos.Character, line.Length);

            // Scan backward to find start of identifier
            int start = col;
            while (start > 0 && IsIdentChar(line[start - 1]))
                start--;

            // Scan forward to find end of identifier
            int end = col;
            while (end < line.Length && IsIdentChar(line[end]))
                end++;

            return line.Substring(start, end - start);
        }

        private static bool IsIdentChar(char c)
            => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Builds a Markdown hover string from a <see cref="DbSymbol"/>.</summary>
        private static string BuildMarkdown(DbSymbol symbol)
        {
            var sb = new StringBuilder();

            // Code block with the prototype
            if (!string.IsNullOrWhiteSpace(symbol.Sourcecode))
            {
                sb.AppendLine("```xsharp");
                sb.AppendLine(symbol.Sourcecode.Trim());
                sb.AppendLine("```");
            }
            else
            {
                // Fallback: just the name
                sb.AppendLine("```xsharp");
                sb.AppendLine(symbol.Name);
                sb.AppendLine("```");
            }

            // XML doc comment (may contain raw XML — strip tags for readability)
            if (!string.IsNullOrWhiteSpace(symbol.XmlComments))
            {
                sb.AppendLine();
                sb.AppendLine(StripXmlTags(symbol.XmlComments.Trim()));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Strips XML tags from a doc-comment string, leaving plain text.
        /// Handles <c>&lt;summary&gt;</c>, <c>&lt;param&gt;</c>, etc.
        /// </summary>
        private static string StripXmlTags(string xml)
        {
            // Simple regex-free approach: remove angle-bracket content
            var sb   = new StringBuilder(xml.Length);
            bool tag = false;
            foreach (char c in xml)
            {
                if      (c == '<') tag = true;
                else if (c == '>') tag = false;
                else if (!tag)     sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
