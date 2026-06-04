using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using XSharpLanguageServer.Models;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Resolves the identifier before a member-access trigger (<c>.</c> or <c>:</c>)
    /// to a type name so that <see cref="XSharpWorkspaceIndex.GetMembersOf"/> can
    /// return the correct set of completion items.
    /// <para>
    /// Resolution order (first match wins):
    /// <list type="number">
    ///   <item><c>SELF</c> → name of the enclosing class.</item>
    ///   <item>Parameter of the enclosing function/method with a matching name.</item>
    ///   <item><c>LOCAL identifier AS Type</c> declaration in the enclosing scope,
    ///         before the cursor.</item>
    ///   <item>Field of the enclosing class with a matching name (implicit SELF).</item>
    ///   <item>Identifier is itself a known type name in the workspace index
    ///         (static / class reference).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Known limitations: chained calls (<c>GetFoo():Bar</c>) are not resolved;
    /// <c>VAR</c>/<c>DYNAMIC</c> locals are not resolved (no explicit type);
    /// assembly-level type members are not available.
    /// </para>
    /// </summary>
    public static class XSharpTypeResolver
    {
        /// <summary>
        /// Attempts to resolve <paramref name="rawIdentifier"/> (the text immediately
        /// before the <c>.</c> or <c>:</c> trigger) to a type name.
        /// Returns <c>null</c> when resolution fails.
        /// </summary>
        public static string? Resolve(
            XSharpParserRuleContext tree,
            Position cursor,
            string rawIdentifier,
            XSharpWorkspaceIndex workspaceIndex)
        {
            if (string.IsNullOrEmpty(rawIdentifier)) return null;

            // SELF → resolve to the enclosing class name.
            if (string.Equals(rawIdentifier, "SELF", StringComparison.OrdinalIgnoreCase))
                return FindEnclosingClassName(tree, cursor);

            // SUPER → resolve to the parent class (InheritsFrom of the enclosing class).
            if (string.Equals(rawIdentifier, "SUPER", StringComparison.OrdinalIgnoreCase))
            {
                var enclosing = FindEnclosingClassName(tree, cursor);
                if (enclosing == null) return null;
                var classSym = workspaceIndex.FindExact(enclosing);
                return classSym?.InheritsFrom is { Length: > 0 } parent
                    ? CleanTypeName(parent)
                    : null;
            }

            // Locate the enclosing function/method and the enclosing class.
            FindEnclosingScope(tree, cursor,
                out var funcCtx,
                out var signature,
                out var enclosingClass);

            // 1. Parameters of the enclosing function/method.
            if (signature?.ParamList?._Params != null)
            {
                foreach (var p in signature.ParamList._Params)
                {
                    if (p.Id == null) continue;
                    if (!string.Equals(p.Id.GetText(), rawIdentifier,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    var t = CleanTypeName(p.Type?.GetText());
                    if (t != null) return t;
                }
            }

            // 2. LOCAL declarations in the function body, before the cursor.
            if (funcCtx != null)
            {
                var localType = FindLocalType(funcCtx, cursor, rawIdentifier, workspaceIndex);
                if (localType != null) return localType;
            }

            // 3. Field of the enclosing class (implicit SELF reference).
            if (enclosingClass != null)
            {
                foreach (var member in workspaceIndex.GetMembersOf(enclosingClass))
                {
                    if (!string.Equals(member.Name, rawIdentifier,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    var t = CleanTypeName(member.ReturnType);
                    if (t != null) return t;
                }
            }

            var sym = workspaceIndex.FindExact(rawIdentifier);
            if (sym != null)
            {
                // 4. Identifier IS a known type name (static / class reference).
                if (IsTypeKind(sym.Kind))
                    return rawIdentifier;

                // 5. Identifier is a callable — return its declared return type.
                //    Supports chained-call completion: GetFoo():Bar.
                if (!string.IsNullOrEmpty(sym.ReturnType))
                    return CleanTypeName(sym.ReturnType);
            }

            return null;
        }

        // ====================================================================
        // Scope discovery
        // ====================================================================

        private static void FindEnclosingScope(
            XSharpParserRuleContext root,
            Position cursor,
            out XSharpParserRuleContext? funcCtx,
            out XSharpParser.SignatureContext? signature,
            out string? enclosingClass)
        {
            funcCtx       = null;
            signature     = null;
            enclosingClass = null;

            WalkForScope(root, cursor, ref funcCtx, ref signature, ref enclosingClass);
        }

        private static void WalkForScope(
            IParseTree node,
            Position cursor,
            ref XSharpParserRuleContext? funcCtx,
            ref XSharpParser.SignatureContext? signature,
            ref string? enclosingClass)
        {
            if (node is not XSharpParserRuleContext ctx) return;
            if (!ContainsCursor(ctx, cursor)) return;

            switch (ctx)
            {
                case XSharpParser.Class_Context cls when cls.Id != null:
                    enclosingClass = cls.Id.GetText();
                    break;

                case XSharpParser.FuncprocContext fp when fp.Sig != null:
                    funcCtx   = fp;
                    signature = fp.Sig;
                    break;

                case XSharpParser.MethodContext m when m.Sig != null:
                    funcCtx   = m;
                    signature = m.Sig;
                    break;
            }

            for (int i = 0; i < node.ChildCount; i++)
                WalkForScope(node.GetChild(i), cursor, ref funcCtx, ref signature, ref enclosingClass);
        }

        private static string? FindEnclosingClassName(XSharpParserRuleContext root, Position cursor)
        {
            string? result = null;
            WalkForClassName(root, cursor, ref result);
            return result;
        }

        private static void WalkForClassName(IParseTree node, Position cursor, ref string? result)
        {
            if (node is not XSharpParserRuleContext ctx) return;
            if (!ContainsCursor(ctx, cursor)) return;

            if (ctx is XSharpParser.Class_Context cls && cls.Id != null)
                result = cls.Id.GetText();

            for (int i = 0; i < node.ChildCount; i++)
                WalkForClassName(node.GetChild(i), cursor, ref result);
        }

        // ====================================================================
        // LOCAL variable search
        // ====================================================================

        private static string? FindLocalType(
            XSharpParserRuleContext funcCtx,
            Position cursor,
            string identifier,
            XSharpWorkspaceIndex workspaceIndex)
        {
            return WalkForLocal(funcCtx, cursor, identifier, workspaceIndex);
        }

        private static string? WalkForLocal(
            IParseTree node,
            Position cursor,
            string identifier,
            XSharpWorkspaceIndex workspaceIndex)
        {
            if (node is not XSharpParserRuleContext ctx) return null;

            int declLine = Math.Max(0, ctx.Start.Line - 1);

            // ── LOCAL foo AS SomeClass  /  LOCAL foo := expr ──────────────
            if (ctx is XSharpParser.CommonLocalDeclContext decl
                && declLine < cursor.Line
                && decl._LocalVars != null)
            {
                foreach (var lv in decl._LocalVars)
                {
                    if (lv.Id == null) continue;
                    if (!string.Equals(lv.Id.GetText(), identifier,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    // Explicit AS clause — most reliable.
                    var t = CleanTypeName(lv.DataType?.GetText());
                    if (t != null) return t;

                    // Initializer inference: LOCAL foo := SomeClass{} or := GetFoo().
                    if (lv.Expression != null)
                    {
                        var inferred = InferTypeFromExpression(lv.Expression, workspaceIndex);
                        if (inferred != null) return inferred;
                    }
                }
            }

            // ── VAR foo := expr  /  LOCAL foo := expr (implied) ──────────
            if (ctx is XSharpParser.VarLocalDeclContext varDecl
                && declLine < cursor.Line
                && varDecl._ImpliedVars != null)
            {
                foreach (var iv in varDecl._ImpliedVars)
                {
                    if (iv.Id == null) continue;
                    if (!string.Equals(iv.Id.GetText(), identifier,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    if (iv.Expression != null)
                    {
                        var inferred = InferTypeFromExpression(iv.Expression, workspaceIndex);
                        if (inferred != null) return inferred;
                    }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                var result = WalkForLocal(node.GetChild(i), cursor, identifier, workspaceIndex);
                if (result != null) return result;
            }

            return null;
        }

        /// <summary>
        /// Walks an initializer expression subtree looking for:
        /// <list type="bullet">
        ///   <item><c>CtorCallContext</c> — <c>SomeClass{}</c> or <c>SomeClass()</c>;
        ///         the type is read directly from <c>Type</c>.</item>
        ///   <item><c>MethodCallContext</c> — <c>GetFoo()</c>; the return type of
        ///         <c>GetFoo</c> is looked up in the workspace index.</item>
        /// </list>
        /// Returns <c>null</c> when the expression cannot be resolved.
        /// </summary>
        private static string? InferTypeFromExpression(
            IParseTree node,
            XSharpWorkspaceIndex workspaceIndex)
        {
            if (node is XSharpParser.CtorCallContext ctor)
                return CleanTypeName(ctor.Type?.GetText());

            if (node is XSharpParser.MethodCallContext mc)
            {
                string callee = SimpleName(mc.Expr?.GetText() ?? string.Empty);
                if (!string.IsNullOrEmpty(callee))
                {
                    var sym = workspaceIndex.FindExact(callee);
                    return CleanTypeName(sym?.ReturnType);
                }
            }

            // Recurse into child nodes (e.g. PrimaryExpressionContext wrapper).
            for (int i = 0; i < node.ChildCount; i++)
            {
                var result = InferTypeFromExpression(node.GetChild(i), workspaceIndex);
                if (result != null) return result;
            }

            return null;
        }

        private static string SimpleName(string expr)
        {
            int colon = expr.LastIndexOf(':');
            int dot   = expr.LastIndexOf('.');
            int sep   = Math.Max(colon, dot);
            return sep >= 0 ? expr[(sep + 1)..] : expr;
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static bool ContainsCursor(XSharpParserRuleContext ctx, Position cursor)
        {
            if (ctx.Start == null) return false;
            int startLine = Math.Max(0, ctx.Start.Line - 1);
            int stopLine  = ctx.Stop != null ? Math.Max(0, ctx.Stop.Line - 1) : startLine;
            return cursor.Line >= startLine && cursor.Line <= stopLine;
        }

        /// <summary>
        /// Strips nullable <c>?</c>, array <c>[]</c> suffixes, and generic
        /// type parameters from a raw type text so it can be used as a
        /// <see cref="XSharpWorkspaceIndex.GetMembersOf"/> key.
        /// </summary>
        private static string? CleanTypeName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Strip generic parameters: List<Int> → List
            int lt = raw.IndexOf('<');
            if (lt > 0) raw = raw[..lt];

            // Strip array brackets and nullable suffix
            raw = raw.TrimEnd('?', '[', ']', ' ');

            // Take the last segment of a qualified name: System.String → String
            int dot = raw.LastIndexOf('.');
            if (dot >= 0) raw = raw[(dot + 1)..];

            return string.IsNullOrEmpty(raw) ? null : raw.Trim();
        }

        private static bool IsTypeKind(int kind) =>
            kind == XSharpSymbolKind.Class     ||
            kind == XSharpSymbolKind.Interface ||
            kind == XSharpSymbolKind.Structure ||
            kind == XSharpSymbolKind.Enum      ||
            kind == XSharpSymbolKind.Delegate;
    }
}
