# XSharp LSP Server

A Language Server Protocol (LSP) server for the [XSharp](https://www.xsharp.eu) programming language, compatible with any LSP-capable editor (VS Code, Neovim, Visual Studio, etc.).

The server uses the official `XSharp.VSParser.dll` lexer/parser from the XSharp Visual Studio extension to ensure accurate, dialect-aware language analysis.

---

## Features

### Implemented

- **Semantic syntax highlighting** — tokens classified into: keyword, type, modifier, macro (preprocessor directives), comment, string, number, operator, variable
- **Diagnostics** — syntax errors and warnings from the XSharp parser are pushed to the editor as squiggly underlines (`textDocument/publishDiagnostics`)
- **Document synchronization** — full incremental sync (open / change / save / close) with correct `\r\n` and `\n` line ending handling (`textDocument/didOpen`, `didChange`, `didSave`, `didClose`)
- **Document symbols** — hierarchical outline of all declared entities (namespaces, classes, interfaces, structs, enums, functions, methods, properties, events, fields, …) for the outline panel and `Ctrl+Shift+O` navigation (`textDocument/documentSymbol`)
- **Folding ranges** — collapse type declarations, member declarations, control-flow blocks (`IF`, `FOR`, `FOREACH`, `WHILE`, `REPEAT`, `DO`/`DO WHILE`/`DO CASE`, `SWITCH`, `TRY`, `WITH`), `#region`/`#endregion` pairs, and multi-line comments (`textDocument/foldingRange`)
- **Completion** — keywords (from lexer vocabulary) + document symbols from the current file + cross-file type/member lookup from the IntelliSense database, filtered by typed prefix; member completion after `.` and `:` (`textDocument/completion`)
- **Hover** — prototype and XML doc comments for the symbol under the cursor, sourced from the IntelliSense database (`textDocument/hover`)
- **Go to definition** — jumps to the file and line where a symbol is declared, sourced from the IntelliSense database (`textDocument/definition`)
- **Signature help** — parameter hints for all overloads of a function call, sourced from the IntelliSense database (`textDocument/signatureHelp`)
- **Configurable dialect and include paths** — dialect (Core, VO, Vulcan, Harbour, …), include paths, and preprocessor symbols read from `workspace/didChangeConfiguration`; changes trigger a full reparse (`workspace/didChangeConfiguration`)
- **Find references** — locates all usages of the identifier under the cursor across all currently open documents; declaration sites also returned from the IntelliSense database when requested (`textDocument/references`)
- **Rename symbol** — renames every occurrence of the identifier under the cursor across all currently open documents; returns a `WorkspaceEdit` for atomic client-side apply (`textDocument/rename`)
- **Document formatting** — uppercases all XSharp keywords to their canonical spelling and normalises indentation; handles sequential member declarations, single-line and multi-line PROPERTY forms, GET/SET accessor blocks, and all `END` / `END X` terminator variants; uses client-supplied tab size / insert-spaces options; keyword map built automatically from `XSharpLexer` reflection (`textDocument/formatting`)
- **Auto-reconnect to IntelliSense database** — a `FileSystemWatcher` monitors the `.vs/` subtree for `X#Model.xsdb` Created/Changed events and reconnects automatically when VS flushes a new copy of the database (typically every ~5 minutes) or when the file first appears after server startup

### Planned

- Workspace-wide file index — references, rename, code lens, and inlay hints currently only cover open documents; a background scanner will extend coverage to all project files
- `textDocument/selectionRange` — smart expand/shrink selection (Alt+Shift+→)
- `textDocument/onTypeFormatting` — auto-indent on structural keywords
- Call hierarchy (`prepareCallHierarchy`, incoming/outgoing calls)
- Code actions — fix keyword casing, add USING namespace
- Semantic diagnostics — type errors, wrong argument counts, unknown identifiers

---

## Architecture

The server is built on [OmniSharp.Extensions.LanguageServer](https://github.com/OmniSharp/csharp-language-server-protocol) (v0.19.9) and targets **.NET 8**.

### Project structure

```
XSharpLanguageServer/
├── Program.cs               — server bootstrap, DI wiring, Serilog logging
├── Handlers/                — one file per LSP request/notification
│   ├── XSharpTextDocumentSyncHandler.cs
│   ├── XSharpSemanticTokensHandler.cs
│   ├── XSharpDocumentSymbolHandler.cs
│   ├── XSharpFoldingRangeHandler.cs
│   ├── XSharpCompletionHandler.cs
│   ├── XSharpHoverHandler.cs
│   ├── XSharpGoToDefinitionHandler.cs
│   ├── XSharpSignatureHelpHandler.cs
│   ├── XSharpDidChangeConfigurationHandler.cs
│   ├── XSharpReferencesHandler.cs
│   ├── XSharpRenameHandler.cs
│   └── XSharpFormattingHandler.cs
├── Services/                — singleton services shared by all handlers
│   ├── XSharpDocumentService.cs       — text buffer + parse cache + FindTokenLocations
│   ├── XSharpDatabaseService.cs       — read-only access to X#Model.xsdb + auto-reconnect
│   ├── XSharpDiagnosticsPublisher.cs  — pushes diagnostics to the client
│   └── XSharpConfigurationService.cs  — workspace settings + XSharpParseOptions factory
└── Models/
    ├── DbSymbol.cs                — data-transfer object returned by database queries
    └── XSharpWorkspaceSettings.cs — workspace configuration DTO
```

### Component responsibilities

| Component | Namespace | Role |
|---|---|---|
| `Program.cs` | `XSharpLanguageServer` | Server bootstrap, DI wiring, Serilog logging |
| `XSharpDocumentService` | `.Services` | Central singleton: text buffer + parse cache (token stream, parse tree, diagnostics) per document; `FindTokenLocations()` shared helper for references and rename |
| `XSharpDatabaseService` | `.Services` | Read-only SQLite access to `X#Model.xsdb`; located automatically from the LSP `rootUri`; `FileSystemWatcher` reconnects automatically when VS refreshes the file |
| `XSharpConfigurationService` | `.Services` | Parses `workspace/didChangeConfiguration` payload; builds `XSharpParseOptions` from dialect, include paths, and preprocessor symbols |
| `XSharpDiagnosticsPublisher` | `.Services` | Pushes errors/warnings to the client after each parse |
| `XSharpTextDocumentSyncHandler` | `.Handlers` | Handles `didOpen/Change/Save/Close`, triggers re-parse |
| `XSharpSemanticTokensHandler` | `.Handlers` | Reads parse cache, maps tokens to LSP semantic token types |
| `XSharpDocumentSymbolHandler` | `.Handlers` | Walks parse tree, returns hierarchical `DocumentSymbol[]` for outline and breadcrumbs |
| `XSharpFoldingRangeHandler` | `.Handlers` | Derives fold ranges from parse tree nodes, `#region`/`#endregion` pairs, and multi-line comments |
| `XSharpCompletionHandler` | `.Handlers` | Keywords + in-file symbols + cross-file DB lookup; member completion after `.` / `:` |
| `XSharpHoverHandler` | `.Handlers` | Prototype + XML doc comment for the word under the cursor |
| `XSharpGoToDefinitionHandler` | `.Handlers` | File + line from the DB for the word under the cursor |
| `XSharpSignatureHelpHandler` | `.Handlers` | All overloads of the enclosing call from the DB |
| `XSharpDidChangeConfigurationHandler` | `.Handlers` | Applies updated workspace settings and triggers a full reparse |
| `XSharpReferencesHandler` | `.Handlers` | Scans open-document token streams for usages; adds DB declaration sites when requested |
| `XSharpRenameHandler` | `.Handlers` | Finds all token occurrences via `FindTokenLocations()`, returns `WorkspaceEdit` |
| `XSharpFormattingHandler` | `.Handlers` | Uppercases keywords, normalises indentation; keyword map built from `XSharpLexer` reflection |
| `DbSymbol` | `.Models` | DTO returned by all database query methods |
| `XSharpWorkspaceSettings` | `.Models` | DTO for dialect, include paths, and preprocessor symbols |

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
- .NET 8 SDK

The project references `XSharp.VSParser.dll` from the default extension install path:
```
C:\Program Files (x86)\XSharp\Extension\Project\XSharp.VSParser.dll
```

---

## Logging

Set the environment variable `XSHARPLSP_LOG_PATH` to a directory path to enable file logging:

```
set XSHARPLSP_LOG_PATH=C:\Logs
```

A rolling daily log file `XSharpLSPServer.log` will be written to that directory.
Without the variable, log output goes to the debug output only.
