# XSharp LSP Server

A Language Server Protocol (LSP) server for the [XSharp](https://www.xsharp.eu) programming language, compatible with any LSP-capable editor (VS Code, Neovim, Visual Studio, etc.).

The server uses the official `XSharp.VSParser.dll` lexer/parser from the XSharp Visual Studio extension to ensure accurate, dialect-aware language analysis.

---

## Features

### Implemented

- **Semantic syntax highlighting** — tokens classified into: keyword, type, modifier, macro (preprocessor directives), comment, string, number, operator, variable
- **Diagnostics** — syntax errors and warnings from the XSharp parser are pushed to the editor as squiggly underlines (`textDocument/publishDiagnostics`); `#warning` directives emitted via UDC chains in header files (e.g. unsupported-command stubs) are remapped to the actual source line rather than the header line where the UDC replacement is defined
- **Semantic diagnostics** — optional lightweight semantic analysis pass that publishes extra diagnostics (wrong argument count, unknown calls); controlled by `xsharp.semanticDiagnostics` and `xsharp.warnOnUndefinedCalls` workspace settings
- **Document synchronization** — full incremental sync (open / change / save / close) with correct `\r\n` and `\n` line ending handling (`textDocument/didOpen`, `didChange`, `didSave`, `didClose`)
- **File-system watch** — new and deleted source files trigger automatic index updates (`workspace/didChangeWatchedFiles`); `.prg`, `.prgx`, and `.ch` files are watched
- **Document symbols** — hierarchical outline of all declared entities (namespaces, classes, interfaces, structs, enums, functions, methods, properties, events, fields, …) for the outline panel and `Ctrl+Shift+O` navigation (`textDocument/documentSymbol`)
- **Folding ranges** — collapse type declarations, member declarations, control-flow blocks (`IF`, `FOR`, `FOREACH`, `WHILE`, `REPEAT`, `DO`/`DO WHILE`/`DO CASE`, `SWITCH`, `TRY`, `WITH`), `#region`/`#endregion` pairs, and multi-line comments (`textDocument/foldingRange`)
- **Selection range** — smart expand / shrink selection following XSharp block structure (Alt+Shift+→ in VS Code) (`textDocument/selectionRange`)
- **Completion** — keywords + document symbols + cross-file workspace index lookup; DB queried as assembly-only fallback; member completion after `.` and `:` with local type inference, assembly member reflection (`STRING:Length`, `INT:ToString()`, etc.), and chained call return-type resolution (`GetFoo():Bar`) (`textDocument/completion`)
- **Hover** — prototype and XML doc comments for the symbol under the cursor; workspace index queried first, IntelliSense database as fallback (`textDocument/hover`)
- **Go to definition** — jumps to the declaration in workspace source files or, for assembly symbols, to the DB entry (`textDocument/definition`)
- **Signature help** — parameter hints for all overloads of a function call (`textDocument/signatureHelp`)
- **Find references** — locates all usages across the entire project via the workspace token index (not limited to open documents); declaration sites also returned when requested (`textDocument/references`)
- **Rename symbol** — renames every occurrence across all project files (open and closed) via the workspace index; scope-aware: `LOCAL`/`VAR` variables, parameters, `MEMVAR`/`PARAMETERS` (Clipper-style) are scoped to the enclosing function; global symbols, types, and members are renamed project-wide; `textDocument/prepareRename` validates the target before prompting (`textDocument/rename`)
- **Document formatting** — uppercases all XSharp keywords to their canonical spelling and normalises indentation; handles sequential member declarations, single-line and multi-line PROPERTY forms, GET/SET accessor blocks, `DO CASE`/`SWITCH` two-level container/body model, `DO WHILE` loops, VFP `DEFINE CLASS`/`ENDDEFINE`, access-modifier prefixes (`PUBLIC FUNCTION`, `PROTECTED METHOD`, …), multi-line continuation (`;`), and all `END` / `END X` terminator variants; re-lexes with stddefs suppressed so UDC expansions never corrupt the token stream; uses client-supplied tab size / insert-spaces options; keyword map built automatically from `XSharpLexer` reflection (`textDocument/formatting`)
- **On-type formatting** — auto-indents the current line as structural keywords are completed (`textDocument/onTypeFormatting`)
- **Formatting settings** — full set of indentation and keyword-casing options exposed as VS Code workspace settings: `IndentCaseLabel`, `IndentCaseContent`, `IndentBlockContent`, `IndentEntityContent`, `IndentFieldContent`, `IndentNamespace`, `IndentMultiLines`, `KeywordCase`, `TrimTrailingWhitespace`, `InsertFinalNewline`
- **Code lens** — reference counts displayed above every declaration (`textDocument/codeLens`)
- **Inlay hints** — parameter name annotations at call sites (`textDocument/inlayHint`)
- **Workspace symbols** — symbol search across all project source files (`workspace/symbol`)
- **Call hierarchy** — navigate callers and callees of any function or method; uses the workspace index for full-project search (`callHierarchy/prepare`, `callHierarchy/incomingCalls`, `callHierarchy/outgoingCalls`)
- **Code actions** — *Add USING*: inserts a missing `USING` directive; *Fix keyword casing*: corrects keyword case to match the configured `KeywordCase` setting (`textDocument/codeAction`)
- **Configurable dialect, include paths, preprocessor symbols, and standard definitions** — read from `workspace/didChangeConfiguration` (`xsharp.dialect`, `xsharp.includePaths`, `xsharp.preprocessorSymbols`, `xsharp.standardDefs`); changes trigger a full reparse; all options are passed directly to `XSharpParseOptions.FromVsValues` using the bare-name form required by the internal argument parser
- **Project settings auto-detection** — on startup `XSharpWorkspaceScanner` reads the workspace `.xsproj` and extracts `<Dialect>`, `<IncludePaths>`, and `<StandardDefs>` as defaults for any field not already set by the client; FoxPro projects are typically configured automatically without manual VS Code settings
- **Auto-reconnect to IntelliSense database** — a `FileSystemWatcher` monitors the `.vs/` subtree for `X#Model.xsdb` Created/Changed events and reconnects automatically when VS flushes a new copy or when the file first appears after startup

### Planned

- Deeper type resolution — FOREACH variable types

---

## Architecture

The server is built on [OmniSharp.Extensions.LanguageServer](https://github.com/OmniSharp/csharp-language-server-protocol) (v0.19.9) and targets **.NET 10**.

### Project structure

```
XSharpLanguageServer/
├── Program.cs               — server bootstrap, DI wiring, Serilog logging
├── LspWindowSink.cs         — Serilog sink that forwards log events to the VS Code Output panel
├── Handlers/                — one file per LSP request/notification
│   ├── XSharpTextDocumentSyncHandler.cs
│   ├── XSharpSemanticTokensHandler.cs
│   ├── XSharpDocumentSymbolHandler.cs
│   ├── XSharpFoldingRangeHandler.cs
│   ├── XSharpSelectionRangeHandler.cs
│   ├── XSharpCompletionHandler.cs
│   ├── XSharpHoverHandler.cs
│   ├── XSharpGoToDefinitionHandler.cs
│   ├── XSharpSignatureHelpHandler.cs
│   ├── XSharpDidChangeConfigurationHandler.cs
│   ├── XSharpDidChangeWatchedFilesHandler.cs
│   ├── XSharpReferencesHandler.cs
│   ├── XSharpRenameHandler.cs
│   ├── XSharpPrepareRenameHandler.cs
│   ├── XSharpFormattingHandler.cs
│   ├── XSharpOnTypeFormattingHandler.cs
│   ├── XSharpCodeActionHandler.cs
│   ├── XSharpCodeLensHandler.cs
│   ├── XSharpInlayHintsHandler.cs
│   ├── XSharpWorkspaceSymbolHandler.cs
│   └── XSharpCallHierarchyHandler.cs
├── Services/                — singleton services shared by all handlers
│   ├── XSharpDocumentService.cs           — text buffer + parse cache + FindTokenLocations
│   ├── XSharpDatabaseService.cs           — read-only access to X#Model.xsdb + auto-reconnect (assembly symbols only)
│   ├── XSharpWorkspaceIndex.cs            — in-memory symbol index built from source files
│   ├── XSharpWorkspaceScanner.cs          — background scanner; populates the workspace index on startup and after saves
│   ├── IndexSymbolExtractor.cs            — extracts symbols and identifier token locations from a parsed file
│   ├── XSharpTypeResolver.cs              — local variable type inference + chained call return-type resolution
│   ├── XSharpScopeHelper.cs               — shared scope utilities (enclosing function, locals, parameters, MEMVAR)
│   ├── XSharpSemanticDiagnosticsService.cs — optional semantic analysis pass (argument counts, unknown calls)
│   ├── XSharpDiagnosticsPublisher.cs      — pushes diagnostics to the client
│   └── XSharpConfigurationService.cs      — workspace settings + XSharpParseOptions factory
└── Models/
    ├── DbSymbol.cs                — DTO returned by database queries
    ├── WorkspaceSymbol.cs         — DTO for workspace index symbols
    ├── IdentifierLocation.cs      — token location entry in the per-file identifier map
    ├── XSharpSymbolKind.cs        — symbol kind enum used by the workspace index
    └── XSharpWorkspaceSettings.cs — workspace configuration DTO
```

### Component responsibilities

| Component | Namespace | Role |
|---|---|---|
| `Program.cs` | `XSharpLanguageServer` | Server bootstrap, DI wiring, Serilog logging |
| `LspWindowSink` | `XSharpLanguageServer` | Serilog `ILogEventSink` that forwards `Information+` events to the client via `window/logMessage` |
| `XSharpDocumentService` | `.Services` | Central singleton: text buffer + parse cache (token stream, parse tree, diagnostics) per document; `FindTokenLocations()` shared helper |
| `XSharpDatabaseService` | `.Services` | Read-only SQLite access to `X#Model.xsdb`; scoped to assembly-only symbols; `FileSystemWatcher` reconnects automatically when VS refreshes the file |
| `XSharpWorkspaceIndex` | `.Services` | In-memory index of all project symbols and per-file identifier token locations; primary lookup for all handlers |
| `XSharpWorkspaceScanner` | `.Services` | Background service that parses all source files at startup and after each save; feeds `XSharpWorkspaceIndex`; reads `.xsproj` to auto-apply `<Dialect>`, `<IncludePaths>`, and `<StandardDefs>` as project defaults |
| `IndexSymbolExtractor` | `.Services` | Extracts `WorkspaceSymbol` entries and identifier token locations from a single parsed file |
| `XSharpTypeResolver` | `.Services` | Infers local variable types from assignments and member access chains; resolves chained call return types via workspace index and DB assembly overloads |
| `XSharpScopeHelper` | `.Services` | Shared static scope utilities: enclosing function discovery, local/parameter/MEMVAR classification; used by rename and code actions |
| `XSharpSemanticDiagnosticsService` | `.Services` | Optional semantic analysis: wrong argument count, unknown call warnings; runs after each parse when enabled |
| `XSharpConfigurationService` | `.Services` | Parses `workspace/didChangeConfiguration` payload; builds `XSharpParseOptions` from dialect, include paths, standard-defs header, and preprocessor symbols; `GetFormattingParseOptions()` returns options with stddefs suppressed for safe formatter re-lex |
| `XSharpDiagnosticsPublisher` | `.Services` | Pushes errors/warnings to the client after each parse |
| `XSharpTextDocumentSyncHandler` | `.Handlers` | Handles `didOpen/Change/Save/Close`, triggers re-parse and index update |
| `XSharpSemanticTokensHandler` | `.Handlers` | Reads parse cache, maps tokens to LSP semantic token types |
| `XSharpDocumentSymbolHandler` | `.Handlers` | Walks parse tree, returns hierarchical `DocumentSymbol[]` for outline and breadcrumbs |
| `XSharpFoldingRangeHandler` | `.Handlers` | Derives fold ranges from parse tree nodes, `#region`/`#endregion` pairs, and multi-line comments |
| `XSharpSelectionRangeHandler` | `.Handlers` | Returns nested selection ranges following XSharp block structure |
| `XSharpCompletionHandler` | `.Handlers` | Keywords + workspace index + DB assembly fallback; local type inference for member completion |
| `XSharpHoverHandler` | `.Handlers` | Prototype + XML doc comment; workspace index first, DB assembly fallback |
| `XSharpGoToDefinitionHandler` | `.Handlers` | Declaration lookup in workspace index; DB for assembly symbols |
| `XSharpSignatureHelpHandler` | `.Handlers` | All overloads of the enclosing call from the workspace index / DB |
| `XSharpDidChangeConfigurationHandler` | `.Handlers` | Applies updated workspace settings and triggers a full reparse |
| `XSharpDidChangeWatchedFilesHandler` | `.Handlers` | Reacts to file creation/deletion events; updates workspace index accordingly |
| `XSharpReferencesHandler` | `.Handlers` | Full-project reference search via workspace identifier token map |
| `XSharpRenameHandler` | `.Handlers` | Renames across all project files via workspace index; returns `WorkspaceEdit` |
| `XSharpPrepareRenameHandler` | `.Handlers` | Validates rename target and returns the token range before the editor prompts the user |
| `XSharpFormattingHandler` | `.Handlers` | Uppercases keywords, normalises indentation per formatting settings |
| `XSharpOnTypeFormattingHandler` | `.Handlers` | Auto-indents on structural keyword completion |
| `XSharpCodeActionHandler` | `.Handlers` | Produces *Add USING* and *Fix keyword casing* code actions |
| `XSharpCodeLensHandler` | `.Handlers` | Reference count annotations above declarations |
| `XSharpInlayHintsHandler` | `.Handlers` | Parameter name annotations at call sites |
| `XSharpWorkspaceSymbolHandler` | `.Handlers` | Project-wide symbol search via workspace index |
| `XSharpCallHierarchyHandler` | `.Handlers` | Prepares call hierarchy and resolves incoming/outgoing calls via workspace index |
| `DbSymbol` | `.Models` | DTO returned by database query methods |
| `WorkspaceSymbol` | `.Models` | DTO for workspace index symbols |
| `IdentifierLocation` | `.Models` | Token location entry in the per-file identifier map |
| `XSharpSymbolKind` | `.Models` | Symbol kind enum used by the workspace index |
| `XSharpWorkspaceSettings` | `.Models` | DTO for all workspace configuration settings (dialect, include paths, standard-defs header, preprocessor symbols, indentation, keyword case, …) |

### Parse pipeline

```
DidOpen / DidChange / DidSave
         │
         ▼
XSharpDocumentService.UpdateText()
         │
         ▼
VsParser.Parse()  ──►  token stream + parse tree + diagnostics
         │
         ├──► parse cache  (read by all handlers)
         │
         └──► XSharpDiagnosticsPublisher  ──►  publishDiagnostics
```

### IntelliSense database (X#Model.xsdb)

The XSharp Visual Studio extension maintains a SQLite database (`X#Model.xsdb`) under `.vs/<solution-name>/` that stores all project symbols (types, members, globals) with their prototypes, file locations, and XML doc comments.

`XSharpDatabaseService` opens this database in **read-only** mode. It is located automatically by walking up from the LSP workspace root to find a `.sln` file. A `FileSystemWatcher` on the `.vs/` subtree detects when VS flushes a fresh copy and reconnects automatically after a 2-second debounce. If the database is not found, all DB-backed features (hover, go-to-definition, signature help, cross-file completion, find references) degrade gracefully — in-file features continue to work.

---

## Requirements

- [XSharp Visual Studio Extension](https://www.xsharp.eu) installed (provides `XSharp.VSParser.dll`)
- .NET 10 SDK

The project references `XSharp.VSParser.dll` from the default extension install path:
```
C:\Program Files (x86)\XSharp\Extension\Project\XSharp.VSParser.dll
```

---

## Logging

Log messages (`Information` and above) are forwarded to the LSP client via
`window/logMessage` and appear in **Output → X# Language Server** in VS Code.

To also write a rolling daily log file, set the environment variable `XSHARPLSP_LOG_PATH`:

```
set XSHARPLSP_LOG_PATH=C:\Logs
```

A file `XSharpLSPServer.log` will be written to that directory (all levels including `Debug`).
Without the variable, `Debug`-level output goes to the debugger output only.
