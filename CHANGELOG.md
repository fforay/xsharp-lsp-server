# Change Log

All notable changes to the XSharp Language Server will be documented in this file.

Check [Keep a Changelog](http://keepachangelog.com/) for recommendations on how to structure this file.

## [Unreleased]

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
