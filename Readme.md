# XSharp LSP Server

A Language Server Protocol (LSP) server for the [XSharp](https://www.xsharp.eu) programming language, compatible with any LSP-capable editor (VS Code, Neovim, Visual Studio, etc.).

The server uses the official `XSharp.VSParser.dll` lexer/parser from the XSharp Visual Studio extension to ensure accurate, dialect-aware language analysis.

---

## Features

### Implemented

- **Semantic syntax highlighting** — tokens classified into: keyword, type, modifier, macro (preprocessor directives), comment, string, number, operator, variable
- **Diagnostics** — syntax errors and warnings from the XSharp parser are pushed to the editor as squiggly underlines (`textDocument/publishDiagnostics`)
- **Document synchronization** — full incremental sync (open / change / save / close) with correct `\r\n` and `\n` line ending handling
- **Document symbols** — hierarchical outline of all declared entities (namespaces, classes, interfaces, structs, enums, functions, methods, properties, events, fields, …) for the outline panel and `Ctrl+Shift+O` navigation (`textDocument/documentSymbol`)
- **Folding ranges** — collapse classes, methods, `#region`/`#endregion` blocks, and multi-line comments (`textDocument/foldingRange`)
- **Completion** — keywords (from lexer vocabulary) + document symbols from the current file + cross-file type/member lookup from the IntelliSense database, filtered by typed prefix; member completion after `.` and `:` (`textDocument/completion`)
- **Hover** — prototype and XML doc comments for the symbol under the cursor, sourced from the IntelliSense database (`textDocument/hover`)
- **Go to definition** — jumps to the file and line where a symbol is declared, sourced from the IntelliSense database (`textDocument/definition`)
- **Signature help** — parameter hints for all overloads of a function call, sourced from the IntelliSense database (`textDocument/signatureHelp`)

### Planned

- Find references *(requires cross-file workspace index)*
- Configurable XSharp dialect and include paths via LSP workspace settings

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
│   └── XSharpSignatureHelpHandler.cs
├── Services/                — singleton services shared by all handlers
│   ├── XSharpDocumentService.cs       — text buffer + parse cache
│   ├── XSharpDatabaseService.cs       — read-only access to X#Model.xsdb
│   └── XSharpDiagnosticsPublisher.cs  — pushes diagnostics to the client
└── Models/
    └── DbSymbol.cs          — data-transfer object returned by database queries
```

### Component responsibilities

| Component | Namespace | Role |
|---|---|---|
| `Program.cs` | `XSharpLanguageServer` | Server bootstrap, DI wiring, Serilog logging |
| `XSharpDocumentService` | `.Services` | Central singleton: text buffer + parse cache (token stream, parse tree, diagnostics) per document |
| `XSharpDatabaseService` | `.Services` | Read-only SQLite access to `X#Model.xsdb`; located automatically from the LSP `rootUri` at initialisation |
| `XSharpDiagnosticsPublisher` | `.Services` | Pushes errors/warnings to the client after each parse |
| `XSharpTextDocumentSyncHandler` | `.Handlers` | Handles `didOpen/Change/Save/Close`, triggers re-parse |
| `XSharpSemanticTokensHandler` | `.Handlers` | Reads parse cache, maps tokens to LSP semantic token types |
| `XSharpDocumentSymbolHandler` | `.Handlers` | Walks parse tree, returns hierarchical `DocumentSymbol[]` for outline and breadcrumbs |
| `XSharpFoldingRangeHandler` | `.Handlers` | Derives fold ranges from parse tree nodes, `#region`/`#endregion` pairs, and multi-line comments |
| `XSharpCompletionHandler` | `.Handlers` | Keywords + in-file symbols + cross-file DB lookup; member completion after `.` / `:` |
| `XSharpHoverHandler` | `.Handlers` | Prototype + XML doc comment for the word under the cursor |
| `XSharpGoToDefinitionHandler` | `.Handlers` | File + line from the DB for the word under the cursor |
| `XSharpSignatureHelpHandler` | `.Handlers` | All overloads of the enclosing call from the DB |
| `DbSymbol` | `.Models` | DTO returned by all database query methods |

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

`XSharpDatabaseService` opens this database in **read-only** mode. It is located automatically by walking up from the LSP workspace root to find a `.sln` file. If the database is not found, all DB-backed features (hover, go-to-definition, signature help, cross-file completion) degrade gracefully — in-file features continue to work.

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
