using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using LanguageService.SyntaxTree.Tree;
using System;
using System.Collections.Generic;
using XSharpLanguageServer.Models;
using BufferedTokenStream = LanguageService.SyntaxTree.BufferedTokenStream;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Walks an XSharp parse tree and extracts every declaration as a flat list of
    /// <see cref="WorkspaceSymbol"/> objects suitable for insertion into
    /// <see cref="XSharpWorkspaceIndex"/>.
    /// <para>
    /// This is intentionally a flat extractor, not a hierarchical one.
    /// <c>XSharpDocumentSymbolHandler</c> maintains its own tree walker
    /// (<c>SymbolWalker</c>) because that handler must produce <c>DocumentSymbol</c>
    /// objects with full parent–child structure and end-position ranges — outputs
    /// that are incompatible with the flat <see cref="WorkspaceSymbol"/> model.
    /// Both walkers recognise the same parse-tree node types.
    /// </para>
    /// </summary>
    public static class IndexSymbolExtractor
    {
        /// <summary>
        /// Extracts all declarations from <paramref name="tree"/> and returns them
        /// as a flat list of <see cref="WorkspaceSymbol"/> objects.
        /// </summary>
        /// <param name="tree">Root of the parse tree (source / foxsource rule).</param>
        /// <param name="filePath">Absolute path stored on each symbol.</param>
        /// <param name="sourceText">
        /// Full source text of the file.  Used to capture the declaration prototype
        /// line for hover tooltips.  May be empty — symbols are still extracted,
        /// but <see cref="WorkspaceSymbol.Sourcecode"/> will be <c>null</c>.
        /// </param>
        public static List<WorkspaceSymbol> Extract(
            XSharpParserRuleContext tree,
            string filePath,
            string sourceText)
        {
            var results = new List<WorkspaceSymbol>();
            var lines   = sourceText.Length > 0
                ? sourceText.Split('\n')
                : Array.Empty<string>();

            Walk(tree, filePath, lines, currentTypeName: null, results);
            return results;
        }

        // ====================================================================
        // Identifier token extraction  (for the usage/reference index)
        // ====================================================================

        // XSharp lexer token type for a plain identifier (non-keyword).
        private const int TokenTypeId = 351;

        /// <summary>
        /// Extracts every identifier token from <paramref name="tokenStream"/> and
        /// returns them as a flat list of <see cref="IdentifierLocation"/> values.
        /// <para>
        /// Used by <c>XSharpWorkspaceScanner</c>, <c>XSharpTextDocumentSyncHandler</c>,
        /// and <c>XSharpDidChangeWatchedFilesHandler</c> to populate the token usage
        /// index in <see cref="XSharpWorkspaceIndex"/> so that
        /// <c>textDocument/references</c> can search across all project files.
        /// </para>
        /// </summary>
        public static List<IdentifierLocation> ExtractIdentifiers(
            ITokenStream tokenStream,
            string filePath)
        {
            var results = new List<IdentifierLocation>();

            if (tokenStream is not BufferedTokenStream buffered) return results;

            var tokens = buffered.GetTokens();
            if (tokens == null) return results;

            foreach (var token in tokens)
            {
                if (token.Type != TokenTypeId) continue;
                if (string.IsNullOrEmpty(token.Text)) continue;

                // XSharp parser uses 1-based lines; LSP uses 0-based.
                results.Add(new IdentifierLocation
                {
                    Text     = token.Text,
                    FilePath = filePath,
                    Line     = Math.Max(0, token.Line - 1),
                    Col      = Math.Max(0, token.Column),
                });
            }

            return results;
        }

        // ====================================================================
        // Tree walker
        // ====================================================================

        private static void Walk(
            IParseTree node,
            string filePath,
            string[] lines,
            string? currentTypeName,
            List<WorkspaceSymbol> results)
        {
            if (node is not XSharpParserRuleContext ctx) return;

            switch (ctx)
            {
                // ── Namespace — not added as a symbol; recurse into children ──
                case XSharpParser.Namespace_Context ns:
                    WalkChildren(ns, filePath, lines, currentTypeName, results);
                    break;

                // ── Type declarations ─────────────────────────────────────────
                case XSharpParser.Class_Context cls when cls.Id != null:
                    Add(results, cls.Id.GetText(), XSharpSymbolKind.Class,
                        returnType: null, cls, filePath, lines, currentTypeName,
                        inheritsFrom: cls.BaseType?.GetText());
                    WalkMembers(cls._Members, filePath, lines, cls.Id.GetText(), results);
                    break;

                case XSharpParser.Interface_Context iface when iface.Id != null:
                    Add(results, iface.Id.GetText(), XSharpSymbolKind.Interface,
                        returnType: null, iface, filePath, lines, currentTypeName);
                    WalkMembers(iface._Members, filePath, lines, iface.Id.GetText(), results);
                    break;

                case XSharpParser.Structure_Context strct when strct.Id != null:
                    Add(results, strct.Id.GetText(), XSharpSymbolKind.Structure,
                        returnType: null, strct, filePath, lines, currentTypeName);
                    WalkMembers(strct._Members, filePath, lines, strct.Id.GetText(), results);
                    break;

                case XSharpParser.Enum_Context en when en.Id != null:
                    Add(results, en.Id.GetText(), XSharpSymbolKind.Enum,
                        returnType: null, en, filePath, lines, currentTypeName);
                    WalkEnumMembers(en, filePath, lines, results);
                    break;

                case XSharpParser.Delegate_Context del when del.Id != null:
                    Add(results, del.Id.GetText(), XSharpSymbolKind.Delegate,
                        returnType: null, del, filePath, lines, currentTypeName);
                    break;

                case XSharpParser.VostructContext vos when vos.Id != null:
                    Add(results, vos.Id.GetText(), XSharpSymbolKind.Structure,
                        returnType: null, vos, filePath, lines, currentTypeName);
                    WalkChildren(vos, filePath, lines, vos.Id.GetText(), results);
                    break;

                case XSharpParser.VounionContext vou when vou.Id != null:
                    Add(results, vou.Id.GetText(), XSharpSymbolKind.Structure,
                        returnType: null, vou, filePath, lines, currentTypeName);
                    WalkChildren(vou, filePath, lines, vou.Id.GetText(), results);
                    break;

                // ── Global functions and procedures ───────────────────────────
                case XSharpParser.FuncprocContext fp when fp.Sig?.Id != null:
                {
                    int kind = fp.T?.Token?.Type == XSharpParser.FUNCTION
                        ? XSharpSymbolKind.Function
                        : XSharpSymbolKind.Procedure;
                    Add(results, fp.Sig.Id.GetText(), kind,
                        fp.Sig.Type?.GetText(), fp, filePath, lines, currentTypeName);
                    break;
                }

                // ── VO / Vulcan globals ───────────────────────────────────────
                case XSharpParser.VodefineContext vodef when vodef.Id != null:
                    Add(results, vodef.Id.GetText(), XSharpSymbolKind.Define,
                        returnType: null, vodef, filePath, lines, currentTypeName);
                    break;

                case XSharpParser.VoglobalContext voglobal when voglobal._Vars != null:
                    foreach (var v in voglobal._Vars)
                        if (v.Id != null)
                            Add(results, v.Id.GetText(), XSharpSymbolKind.Global,
                                returnType: null, voglobal, filePath, lines, currentTypeName);
                    break;

                // ── Class member wrapper — unwrap and re-dispatch ─────────────
                case XSharpParser.ClassmemberContext cm:
                    if (cm.ChildCount > 0 && cm.GetChild(0) is XSharpParserRuleContext inner)
                        Walk(inner, filePath, lines, currentTypeName, results);
                    break;

                // ── Class members ─────────────────────────────────────────────
                case XSharpParser.MethodContext method when method.Sig?.Id != null:
                {
                    int kind = method.T?.Token?.Type switch
                    {
                        XSharpParser.ACCESS => XSharpSymbolKind.Access,
                        XSharpParser.ASSIGN => XSharpSymbolKind.Assign,
                        _                  => XSharpSymbolKind.Method,
                    };
                    Add(results, method.Sig.Id.GetText(), kind,
                        method.Sig.Type?.GetText(), method, filePath, lines, currentTypeName);
                    break;
                }

                case XSharpParser.ConstructorContext ctor:
                    Add(results, "Constructor", XSharpSymbolKind.Constructor,
                        returnType: null, ctor, filePath, lines, currentTypeName);
                    break;

                case XSharpParser.DestructorContext dtor:
                    Add(results, "Destructor", XSharpSymbolKind.Destructor,
                        returnType: null, dtor, filePath, lines, currentTypeName);
                    break;

                case XSharpParser.PropertyContext prop when prop.Id != null:
                    Add(results, prop.Id.GetText(), XSharpSymbolKind.Property,
                        prop.Type?.GetText(), prop, filePath, lines, currentTypeName);
                    break;

                case XSharpParser.Event_Context evt when evt.Id != null:
                    Add(results, evt.Id.GetText(), XSharpSymbolKind.Event,
                        returnType: null, evt, filePath, lines, currentTypeName);
                    break;

                case XSharpParser.ClassvarsContext cvars when cvars._Vars != null:
                    foreach (var v in cvars._Vars)
                        if (v.Id != null)
                            Add(results, v.Id.GetText(), XSharpSymbolKind.Field,
                                returnType: v.DataType?.GetText(), cvars, filePath, lines, currentTypeName);
                    break;

                // ── Anything else — recurse in case it wraps declarations ─────
                default:
                    WalkChildren(ctx, filePath, lines, currentTypeName, results);
                    break;
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static void WalkChildren(
            IParseTree node,
            string filePath,
            string[] lines,
            string? currentTypeName,
            List<WorkspaceSymbol> results)
        {
            for (int i = 0; i < node.ChildCount; i++)
                Walk(node.GetChild(i), filePath, lines, currentTypeName, results);
        }

        private static void WalkMembers(
            IList<XSharpParser.ClassmemberContext>? members,
            string filePath,
            string[] lines,
            string typeName,
            List<WorkspaceSymbol> results)
        {
            if (members == null) return;
            foreach (var cm in members)
                Walk(cm, filePath, lines, typeName, results);
        }

        private static void WalkEnumMembers(
            XSharpParser.Enum_Context en,
            string filePath,
            string[] lines,
            List<WorkspaceSymbol> results)
        {
            if (en._Members == null) return;
            foreach (var m in en._Members)
                if (m is XSharpParser.EnummemberContext em && em.Id != null)
                    Add(results, em.Id.GetText(), XSharpSymbolKind.EnumMember,
                        returnType: null, em, filePath, lines, en.Id.GetText());
        }

        private static void Add(
            List<WorkspaceSymbol> results,
            string name,
            int kind,
            string? returnType,
            XSharpParserRuleContext ctx,
            string filePath,
            string[] lines,
            string? typeName,
            string? inheritsFrom = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            // XSharp parser uses 1-based lines; LSP uses 0-based.
            int startLine = Math.Max(0, ctx.Start.Line - 1);
            int startCol  = Math.Max(0, ctx.Start.Column);

            // Capture the first source line as the declaration prototype.
            // This is what the hover handler displays as the signature.
            string? sourcecode = null;
            if (startLine < lines.Length)
                sourcecode = lines[startLine].TrimEnd('\r').Trim();

            results.Add(new WorkspaceSymbol
            {
                Name        = name,
                Kind        = kind,
                ReturnType  = string.IsNullOrEmpty(returnType) ? null : returnType,
                Sourcecode  = sourcecode,
                FileName    = filePath,
                StartLine   = startLine,
                StartCol    = startCol,
                TypeName    = typeName,
                InheritsFrom = string.IsNullOrEmpty(inheritsFrom) ? null : inheritsFrom,
            });
        }
    }
}
