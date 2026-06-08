# Change Log

All notable changes to the XSharp Language Server will be documented in this file.

Check [Keep a Changelog](http://keepachangelog.com/) for recommendations on how to structure this file.

## [0.6.5] - 2026-06-08

### Added
- **Assembly member completion** — member completion after `.` / `:` now covers BCL and
  NuGet types via .NET reflection (`XSharpDatabaseService.FindAssemblyMembersOf`).
  A 30-entry XSharp-to-.NET alias map resolves language primitives (`STRING` →
  `System.String`, `INT` → `System.Int32`, `LOGIC` → `System.Boolean`, etc.).
  Results are cached indefinitely in a `ConcurrentDictionary`.
- **Chained call member completion** — `GetFoo():Bar` now resolves the return type of
  `GetFoo()` via the workspace index (step 5) and, for assembly-level callables, via
  `XSharpDatabaseService.FindAssemblyOverloads` (step 6 in `XSharpTypeResolver`).
- **`XSharpScopeHelper`** — new shared static utility class for parse-tree scope
  operations: `FindEnclosingFunction`, `IsLocalOrParameter`, `CollectLocalsInRange`,
  `CollectParameterNames`, `CollectClipperParameters`, `CollectPrivateMemvars`.
  Used by `XSharpRenameHandler` and `XSharpCodeActionHandler`.

### Improved
- **Rename symbol — scope awareness** — `LOCAL`/`VAR` variables, signature parameters,
  Clipper-style `PARAMETERS`, and `MEMVAR`/`PRIVATE` variables are now renamed only
  within the enclosing function scope.  `PUBLIC` MEMVAR and all global symbols, types,
  and members continue to be renamed project-wide.  Logged as `scope [N–M]` or
  `project-wide` respectively.
- **`XSharpCodeActionHandler`** — duplicate scope-walking logic (`WalkForFunc`,
  `WalkLocals`, `CollectLocalsInRange`, `CollectLocalsBefore`) replaced with
  delegation to `XSharpScopeHelper`.

## [0.6.0] - 2026-06-05

### Added
- **Workspace symbol index** — background scanner (`XSharpWorkspaceScanner`) parses all source files in the workspace at startup and after each save; symbols are stored in an in-memory `XSharpWorkspaceIndex`; covers `.prg`, `.prgx`, `.ch`, and other XSharp source extensions
- **File-system watch** (`workspace/didChangeWatchedFiles`) — new and deleted source files are detected automatically and trigger incremental index updates; `.prgx` and `.ch` files included in the watch list
- **Two-tier symbol lookup** — completion, hover, go-to-definition, signature help, references, inlay hints, and workspace symbols now query the workspace index first and fall back to the IntelliSense database (`X#Model.xsdb`) for assembly-only symbols; DB queries are now scoped to assembly symbols exclusively
- **Full-project find references** — a per-file identifier token map is built alongside the workspace index, allowing `textDocument/references` to locate usages across all project files, not only open documents
- **Selection range** (`textDocument/selectionRange`) — smart expand / shrink selection (Alt+Shift+→ in VS Code); ranges follow XSharp block structure
- **Call hierarchy** (`callHierarchy/prepare`, `callHierarchy/incomingCalls`, `callHierarchy/outgoingCalls`) — shows callers and callees of any function or method; uses the workspace index for full-project search
- **Code actions** (`textDocument/codeAction`):
  - *Add USING* — inserts a missing `USING` directive at the top of the file when an unresolved type name is under the cursor
  - *Fix keyword casing* — corrects any keyword whose casing does not match the configured `KeywordCase` setting
- **On-type formatting** (`textDocument/onTypeFormatting`) — auto-indents the current line when a structural keyword (e.g. `IF`, `FUNCTION`, `CLASS`, `CASE`) is completed
- **Semantic diagnostics** — lightweight semantic analysis pass (`XSharpSemanticDiagnosticsService`) publishes extra diagnostics after each parse; enabled by the new `xsharp.semanticDiagnostics` workspace setting (off by default); unknown-call warnings gated on `xsharp.warnOnUndefinedCalls`
- **Formatting settings** — full set of indentation and formatting options exposed as VS Code workspace settings: `IndentCaseLabel`, `IndentCaseContent`, `IndentBlockContent`, `IndentEntityContent`, `IndentFieldContent`, `IndentNamespace`, `IndentMultiLines`, `KeywordCase` (Upper / Lower / Title / None), `TrimTrailingWhitespace`, `InsertFinalNewline`

### Improved
- **Type inference** — `XSharpTypeResolver` now resolves local variable types from assignments, method return types, and property types; used by member-completion (`.` / `:`) to filter suggestions to the actual type
- **Rename symbol** — now covers closed files via the workspace index in addition to open documents; `textDocument/prepareRename` handler added so editors can validate the rename target before prompting the user
- **Document formatting** — indentation engine fully rewritten; now correctly handles:
  - Sequential member declarations (FUNCTION, METHOD, CONSTRUCTOR, DESTRUCTOR, PROPERTY, OPERATOR, EVENT, ACCESS, ASSIGN) implicitly closing the previous member body before opening a new one
  - CLASS / INTERFACE / STRUCTURE after an open member body: member is closed before the type block opens
  - Single-line PROPERTY forms (`AUTO`, inline `GET`/`SET`) — marked as members without opening an extra indent level
  - Multi-line PROPERTY with GET/SET accessor blocks — each accessor indented one extra level; `END GET` / `END SET` close it without ending the property
  - Bare `END` (used as WHILE / DO WHILE terminator) and two-token `END SWITCH`, `END WITH`, etc. — all handled as generic one-level closers
  - `ENDCLASS` and `END CLASS` / `END INTERFACE` / `END STRUCTURE` — close the last open member body then the type block; guard prevents over-decrement when no type body was opened (e.g. `IndentEntityContent` and `IndentFieldContent` both false)
  - `DO CASE` / `SWITCH` — two-level container/body model: `CASE` and `OTHERWISE` align with the opener (`IndentCaseLabel=false`) or sit inside it (`IndentCaseLabel=true`); case body content is indented when `IndentCaseContent=true`; `ENDCASE` closes both levels correctly
  - Multi-line continuation (`;`) — continuation lines are indented one extra level when `IndentMultiLines=true`
- **Folding ranges** — control-flow blocks are now foldable: `IF`, `FOR`, `FOREACH`, `WHILE`, `REPEAT`, `DO` / `DO WHILE` / `DO CASE`, `SWITCH`, `TRY`, `WITH`

### Fixed
- Duplicate `TYPEOF` entry in the hover keyword dictionary caused a `TypeInitializationException` on the first hover request, making hover completely non-functional
- Hover now returns the word's LSP `Range` so the client highlights the exact token instead of guessing
- Hover for members now shows the declaring type (*Declared in `ClassName`*) so overloads from different classes are distinguishable
- XML doc comments in hover cards are now properly formatted: `<summary>`, `<param>`, `<returns>`, and `<remarks>` sections are rendered as Markdown, and XML entities (`&lt;`, `&gt;`, `&amp;`, …) are decoded; malformed XML falls back to plain tag-stripping
- Hover now resolves symbols from referenced assemblies (`ReferencedTypes`, `ReferencedGlobals`) in addition to project types and members — BCL and NuGet types are now covered
- CRLF line endings no longer leave a stray `\r` in the extracted word on Windows files
- DB-unavailable state is now logged at Debug level for non-keyword hover misses

## [0.3.0] - 2026-05-11

### Added
- **Code lens** — reference counts displayed above every declaration
- **Inlay hints** — parameter name annotations at call sites
- **Workspace symbols** (`workspace/symbol`) — symbol search across the whole project
- **Find references** (`textDocument/references`) — locates all usages of a symbol
- **Rename symbol** (`textDocument/rename`) — renames a symbol and all its references
- **Document formatting** (`textDocument/formatting`) — uppercases keywords and re-indents the file; string literals and comments are preserved verbatim

### Fixed
- DB connection is now re-established automatically when the database file is detected as stale or replaced
- Completion list no longer contains duplicate entries when the same symbol appears in both the document index and the DB

## [0.2.0] 

### Added
- **XSharpDatabaseService** — reads the XSharp IntelliSense SQLite database (`X#Model.xsdb`) produced by the VS extension; opens it read-only and locates it by walking up from the workspace root to the solution file
- **Hover** (`textDocument/hover`) — shows source signature and XML doc comments for identifiers (from DB) and a description for keywords (static table)
- **Go to definition** (`textDocument/definition`) — navigates to the declaration using `FileName` + `StartLine` from the DB
- **Signature help** (`textDocument/signatureHelp`) — shows parameter lists for functions and methods from the DB
- **Cross-file completion** — DB-powered completion for types, members, and assembly symbols in addition to keywords and in-file symbols
- Configurable XSharp dialect and include paths via workspace settings (`xsharp.dialect`, `xsharp.includePaths`)

## [0.1.0] 

### Added
- Initial release of the XSharp Language Server (OmniSharp / .NET 8, single-file `win-x64` executable)
- **Document sync** — open / change / close / save (`textDocument/didOpen`, `didChange`, `didClose`, `didSave`)
- **Diagnostics** — syntax errors collected via `IErrorListener` and pushed as `textDocument/publishDiagnostics` after every parse
- **Semantic tokens** — keyword, type, modifier, comment, string, number, macro, operator, variable categories
- **Document symbols** (`textDocument/documentSymbol`) — hierarchical outline covering namespaces, classes, interfaces, structs, enums, delegates, functions, procedures, methods, constructors, destructors, properties, events, class variables, VO globals and defines; ACCESS/ASSIGN shown as `Property` with `[Access]`/`[Assign]` suffix
- **Folding ranges** (`textDocument/foldingRange`) — block nodes from the parse tree, `#region`/`#endregion` pairs, and multi-line comments
- **Completion** (`textDocument/completion`) — keyword completion (all XSharp keywords filtered by prefix) and in-file symbol completion (classes, methods, functions, …)
- Shared `XSharpDocumentService` parse cache — all handlers share one parse result per document, re-parsed on every `didChange`/`didSave`
- Structured logging via Serilog (console + file sinks)
