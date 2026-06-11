# Change Log

All notable changes to the XSharp Language Server will be documented in this file.

Check [Keep a Changelog](http://keepachangelog.com/) for recommendations on how to structure this file.

## [0.6.8] - 2026-06-11

### Added
- **Document highlight** (`textDocument/documentHighlight`) — new `XSharpDocumentHighlightHandler`; extracts the word under the cursor, live-scans the open document via `FindTokenLocations()`, and falls back to the workspace index for closed files; returns all occurrences with `DocumentHighlightKind.Text`.  Keyword pair boundaries are returned as `DocumentHighlightKind.Write` (kind 3) so the VS Code extension can apply a distinct decoration.
- **Keyword pair highlighting** (P2-1) — `TryKeywordPairHighlights()` walks the parse tree to find the innermost structural context when the cursor is on a structural keyword; all boundary tokens are returned in a single `DocumentHighlight[]` response.  Sixteen context types covered: `IF`/`ELSEIF`/`ELSE`/`ENDIF`, `FOR`/`NEXT`, `FOREACH`/`NEXT`, `WHILE`/`ENDDO`, `DO WHILE`/`ENDDO`, `REPEAT`/`UNTIL`, `DO CASE`/`CASE`/`OTHERWISE`/`ENDCASE`, `SWITCH`/`CASE`/`OTHERWISE`/`END SWITCH`, `TRY`/`CATCH`/`FINALLY`/`ENDTRY`, `BEGIN SEQUENCE`/`RECOVER`/`END SEQUENCE`, `CLASS`/`ENDCLASS`, `INTERFACE`/`END INTERFACE`, `STRUCTURE`/`END STRUCTURE`, `ENUM`/`END ENUM`, `NAMESPACE`/`END NAMESPACE`, `VOSTRUCT`/`END`, `WITH`/`END WITH`.  Two-token closers (e.g. `END IF`, `END CLASS`) are handled by locating the companion keyword on the same closing line.
- **Snippet completions** (P1-2) — `BuildSnippetItems()` in `XSharpCompletionHandler` provides 30 structured completions ported from the XSharp LanguageService snippet files: `IF`, `IF ELSE`, `FOR`, `FOREACH`, `DO WHILE`, `WHILE`, `REPEAT`, `TRY`/`CATCH`/`FINALLY`, `BEGIN SEQUENCE`, `CLASS`, `INTERFACE`, `STRUCTURE`, `VOSTRUCT`, `PROPERTY`, `SWITCH`, `DO CASE`, `#region`, `#ifdef`, `#ifndef`, and function starters (`start`, `initproc`, `exitproc`).  Snippets are injected before the keyword pass so snippet variants win; `FilterText` enables multi-word labels (e.g. `IF ELSE` shows when typing `IF`).
- **INHERIT / IMPLEMENTS context completion** (P1-3) — `DetectInheritContext()` in `XSharpCompletionHandler` scans the current line backward, strips already-typed names, and recognises `INHERIT` / `IMPLEMENTS` keywords; the early-return branch serves only `Class` (kind 1) or `Interface` (kind 8) symbols from the workspace index and assembly database.
- **Code action — Implement interface** (P3-2) — `ComputeImplementInterfaceActions()` in `XSharpCodeActionHandler`; reads `_Implements` from the parse-tree class context; performs two-tier member lookup (workspace `GetMembersOf` → `FindAssemblyMembersOf`); generates `METHOD`, `PROPERTY` (GET + SET), and `EVENT` stubs via `GenerateMemberStub()`; infers body indentation from existing class content; inserts stubs before `END CLASS`.
- **`xsharp.hoverKeywords` setting** — new `HoverKeywords` property in `XSharpWorkspaceSettings` (default `true`).  When `false`, hovering over a built-in keyword (`IF`, `RETURN`, `CLASS`, …) produces no tooltip; symbol and local-variable hover are unaffected.  Read in `XSharpConfigurationService.Apply()`; `XSharpHoverHandler` injects `XSharpConfigurationService` and guards the keyword-lookup step with the flag.

### Improved
- **Hover for local variables** (P2-2) — `XSharpHoverHandler` now calls `XSharpTypeResolver.FindLocalVarHover()` after a failed keyword/symbol lookup; resolves the word as a `LOCAL`, `VAR`, or parameter declaration and returns a hover card of the form `LOCAL x AS SomeType` or `PARAM x AS SomeType`.

### Fixed
- **Keyword pair highlight persistence** — VS Code's built-in word-occurrence highlighter (300 ms debounce) was overriding the LSP `documentHighlight` response for keywords with an empty result, causing the "flicker and disappear" behaviour.  The fix is two-part: (1) keyword pair highlights now use `DocumentHighlightKind.Write` (kind 3) so the VS Code extension can distinguish them from identifier occurrences; (2) the VS Code extension uses a custom cursor-change listener (bypassing VS Code's provider pipeline) and disables the built-in occurrence highlighter for XSharp files — see extension changelog.

## [0.6.7] - 2026-06-10

### Fixed
- **`#warning` directive position from UDC chains** — when a `#warning` is emitted via
  chained UDC expansions defined in a header file (e.g. `__XPORTERWARNIN__` in `VFPCmd.xh`),
  the VSParser preprocessor reports the warning at the line number of the replacement token
  inside the header, not the original source line.  `ErrorListener` is now a two-phase
  design: VsParser callbacks accumulate raw entries; `BuildDiagnostics(text)` converts them
  after parsing with access to the full source text.  For out-of-range `WRN_WarningDirective`
  entries the command text is extracted from `args[0]` (the first double-quoted segment of
  the raw `#warning` text) and `FindCommandInSource` scans the source lines for a
  case-insensitive `TrimStart` match, placing the squiggle on the actual command (e.g. line 8
  `ON SHUTDOWN` instead of phantom line 47).  Falls back to `(0, 0)` when no match is found.
  Any other out-of-range diagnostic is clamped to `(0, 0)` as a general safety net.
- **`#warning` directive message formatting** — `WRN_WarningDirective` messages now have
  all surrounding double-quote characters removed and collapsed spaces trimmed, producing
  `ON SHUTDOWN clear events  This command is not (yet) supported` instead of the raw
  `"ON SHUTDOWN clear events"" This command is not (yet) supported"`.
- **Semantic tokens and code-action keyword casing re-lex with stddefs disabled** — both
  handlers now call `VsParser.Lex` (stddefs off) directly instead of reading the cached
  parse result, so UDC tokens such as `DO FORM` and `READ EVENTS` are never replaced by
  `UDC_KEYWORD` expansion tokens and are correctly classified as keywords.  String token
  classification adds Guard 1 (skip multi-line tokens — `VsParser.Parse`-path artefacts)
  and Guard 2 (skip `INCOMPLETE_STRING_CONST`) to prevent incorrect string colouring.

### Added
- **Server log output in VS Code** — log messages (`Information` and above) are now
  forwarded to the LSP client via `window/logMessage` and appear in the
  *Output → X# Language Server* panel.  A new `LspWindowSink` Serilog sink is activated
  once the OmniSharp server is built; messages emitted during startup are silently
  discarded rather than blocking initialisation.

## [0.6.6] - 2026-06-09

### Added
- **FoxPro dialect support — `StandardDefs` setting** — new `xsharp.standardDefs` workspace
  setting (mirrors the `<StandardDefs>` MSBuild property and `/stddefs:` compiler switch).
  The path is passed to `XSharpParseOptions.FromVsValues` so the XSharp preprocessor
  automatically includes the nominated header file before every source file, enabling
  project-level UDC definitions (`#command DO FORM`, `#command READ EVENTS`, etc.) to
  suppress spurious `ERR_ParserError` diagnostics.
- **Project settings auto-detection** — `XSharpWorkspaceScanner` now reads the first
  `.xsproj` found in the workspace on startup and extracts `<StandardDefs>`, `<Dialect>`,
  and `<IncludePaths>`. These values are applied as defaults for any field not already
  set via `workspace/didChangeConfiguration`, so most FoxPro projects are correctly
  configured without any manual VS Code settings.
- **`XSharpConfigurationService.GetFormattingParseOptions()`** — returns parse options
  with `nostddefs:true`, preventing the preprocessor from expanding UDC tokens during
  formatting (see formatter fix below).

### Fixed
- **FoxPro dialect not applied** — `BuildOptions` was calling `FromVsValues` with a
  leading `/` (e.g. `/dialect:FoxPro`).  `FromVsValues` extracts the name before `:`,
  giving `/dialect`, which never matched `case "dialect":` in the internal argument
  parser.  All option strings now use the bare form (`dialect:FoxPro`, `i:path`,
  `d:SYM`), so the dialect, include paths, and preprocessor symbols are correctly
  propagated to the VsParser.  As a result, the VsParser now calls `foxsource()` for
  FoxPro files so `*`/`**`/`&&` are natively tokenised as `SL_COMMENT` on the hidden
  channel — no coloring or diagnostic workarounds needed.
- **Formatter — UDC expansion corrupts token stream** — when `stddefs` is active the
  XSharp preprocessor replaces matched UDC tokens (e.g. `DO`, `FORM`) with
  `UDC_KEYWORD` type tokens and injects expansion tokens into the raw lexer stream at
  the same line number, causing the formatter to output `UDC_KEYWORD UDC_KEYWORD ...`.
  The formatter now calls `VsParser.Lex` with `GetFormattingParseOptions()` (stddefs
  suppressed) so the token stream is uncontaminated.
- **Formatter — `DO FORM` / `DO <program>` incorrectly opened a block** — `DO` was
  unconditionally in `_indentOpen`, causing `DO FORM formgrid1` to open an indent level
  that was never closed, indenting all subsequent lines.  `DO` was removed from
  `_indentOpen`; an explicit `isDoWhile` flag now opens a block only when `DO` is
  followed by `WHILE`; `DO CASE` is still handled via the existing `isDoCaseOrSwitch`
  path.
- **Formatter — access modifiers before structural keywords** — lines beginning with
  `PUBLIC FUNCTION`, `PROTECTED METHOD`, etc. had `firstReal = PUBLIC`, so structural
  checks (`isCodeBlockHeader`, `isTypeOpener`, …) missed the actual command token.
  `GetCommandToken()` now skips leading modifier tokens to find the first non-modifier
  structural keyword.
- **Formatter — VFP `DEFINE CLASS` / `ENDDEFINE` not recognised** — `DEFINE CLASS`
  (VFP dialect type opener) and `ENDDEFINE` (type closer) are now handled alongside
  `CLASS` / `ENDCLASS`.
- **Formatter — indentation stripped when no structure detected** — the safety net now
  preserves original leading whitespace and applies keyword casing only, instead of
  calling `TrimStart()` which removed all indentation.
- **Diagnostic message formatting** — `ErrorFacts.GetMessage` returns a bare fallback
  string (e.g. `"message WRN_WarningDirective"`) for some codes.
  `ErrorListener.BuildDiagnostic` now detects the absence of a `{0}` placeholder and
  uses `args` directly as the message text, so `#warning` directives (e.g. from
  `VFPCmd.xh`'s `__XPORTERWARNIN__`) display their actual content.

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
- Initial release of the XSharp Language Server (OmniSharp / .NET, single-file `win-x64` executable)
- **Document sync** — open / change / close / save (`textDocument/didOpen`, `didChange`, `didClose`, `didSave`)
- **Diagnostics** — syntax errors collected via `IErrorListener` and pushed as `textDocument/publishDiagnostics` after every parse
- **Semantic tokens** — keyword, type, modifier, comment, string, number, macro, operator, variable categories
- **Document symbols** (`textDocument/documentSymbol`) — hierarchical outline covering namespaces, classes, interfaces, structs, enums, delegates, functions, procedures, methods, constructors, destructors, properties, events, class variables, VO globals and defines; ACCESS/ASSIGN shown as `Property` with `[Access]`/`[Assign]` suffix
- **Folding ranges** (`textDocument/foldingRange`) — block nodes from the parse tree, `#region`/`#endregion` pairs, and multi-line comments
- **Completion** (`textDocument/completion`) — keyword completion (all XSharp keywords filtered by prefix) and in-file symbol completion (classes, methods, functions, …)
- Shared `XSharpDocumentService` parse cache — all handlers share one parse result per document, re-parsed on every `didChange`/`didSave`
- Structured logging via Serilog (console + file sinks)
