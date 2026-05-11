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
- **Completion** — keyword list (all XSharp keywords and type keywords, filtered by typed prefix) plus document symbols from the current file (`textDocument/completion`)

### Planned

- Hover (keyword descriptions, symbol information)
- Code completion (keywords, members, document symbols)
- Signature help (parameter hints in function calls)
- Go to definition / find references *(requires cross-file workspace index)*

---

## Architecture

The server is built on [OmniSharp.Extensions.LanguageServer](https://github.com/OmniSharp/csharp-language-server-protocol) (v0.19.9) and targets **.NET 8**.

| Component | File | Role |
|---|---|---|
| Entry point | `Program.cs` | Server bootstrap, DI wiring, Serilog logging |
| Document service | `XSharpDocumentService.cs` | Central singleton: text buffer + parse cache (token stream, parse tree, diagnostics) per document |
| Diagnostics publisher | `XSharpDiagnosticsPublisher.cs` | Pushes errors/warnings to the client after each parse |
| Document sync handler | `XSharpTextDocumentSyncHandler.cs` | Handles `didOpen/Change/Save/Close`, triggers re-parse |
| Semantic tokens handler | `XSharpSemanticTokensHandler.cs` | Reads parse cache, maps tokens to LSP semantic token types |
| Document symbol handler | `XSharpDocumentSymbolHandler.cs` | Walks parse tree, returns hierarchical `DocumentSymbol[]` for outline and breadcrumbs |
| Folding range handler | `XSharpFoldingRangeHandler.cs` | Derives fold ranges from parse tree nodes, `#region`/`#endregion` pairs, and multi-line comments |
| Completion handler | `XSharpCompletionHandler.cs` | Keyword list (from lexer vocabulary) + document symbol identifiers, filtered by typed prefix |

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
         ├──► parse cache  (read by SemanticTokensHandler)
         │
         └──► XSharpDiagnosticsPublisher  ──►  publishDiagnostics
```

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
