using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using XSharpLanguageServer.Models;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Resolves the expression before a member-access trigger (<c>.</c> or <c>:</c>)
    /// to a type name so that <see cref="XSharpWorkspaceIndex.GetMembersOf"/> can
    /// return the correct set of completion items.
    /// <para>
    /// The expression may be a single identifier (<c>oObj:</c>) or a chain of
    /// <c>:</c>/<c>.</c>-separated segments, each a plain name or a call
    /// (<c>oObj:GetFoo():GetBar():</c>). The chain is walked left to right:
    /// the first segment is resolved with the rules below, then each following
    /// segment is looked up as a member of the type resolved so far — its
    /// declared return type becomes the type for the next segment.
    /// </para>
    /// <para>
    /// First-segment resolution order (first match wins):
    /// <list type="number">
    ///   <item><c>SELF</c> → name of the enclosing class.</item>
    ///   <item>Parameter of the enclosing function/method with a matching name.</item>
    ///   <item><c>LOCAL identifier AS Type</c> declaration in the enclosing scope,
    ///         before the cursor.</item>
    ///   <item>Field of the enclosing class with a matching name (implicit SELF).</item>
    ///   <item>Identifier is itself a known type name in the workspace index
    ///         (static / class reference).</item>
    ///   <item>Identifier is a callable in the workspace index — return its declared
    ///         return type.</item>
    ///   <item>Identifier is a callable from a referenced assembly — return type looked
    ///         up via <see cref="XSharpDatabaseService.FindAssemblyOverloads"/>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <c>VAR</c>/<c>DYNAMIC</c> locals have no explicit type, so their type is
    /// inferred from the initializer expression (see
    /// <see cref="XSharpDatabaseService.FindAssemblyMembersOf"/> callers via
    /// <c>FindLocalType</c> → <c>InferTypeFromExpression</c>): constructor calls,
    /// call chains (<c>SELF:GetFoo():GetBar()</c>, with each segment's return type
    /// feeding the next), and bare literals (<c>"abc"</c> → <c>STRING</c>,
    /// <c>123</c> → <c>INT</c>, …). Plain-identifier initializers
    /// (<c>VAR x := someOtherLocal</c>) are deliberately not resolved — doing so
    /// would require re-entering local-variable lookup against the same cursor,
    /// risking infinite recursion for self-referential / mutually-referential
    /// declarations. Chain walking stops at the first segment whose member cannot
    /// be found or whose return type is unknown.
    /// </para>
    /// </summary>
    /// <summary>
    /// Describes a LOCAL variable or function/method parameter found in scope,
    /// used to build the hover card for that variable.
    /// </summary>
    public sealed record LocalVarHoverInfo(
        string  Name,
        string? ExplicitType,   // null when no AS clause (VAR / no-type LOCAL)
        bool    IsVar,          // true for VAR / LOCAL IMPLIED declarations
        bool    IsParam,        // true for function/method parameters
        string? InferredType);  // non-null when IsVar=true and initializer was resolved

    public static class XSharpTypeResolver
    {
        /// <summary>
        /// Attempts to resolve <paramref name="rawIdentifier"/> — the member-access
        /// expression immediately before the <c>.</c> or <c>:</c> trigger, which may
        /// be a single identifier or a <c>:</c>/<c>.</c>-separated chain such as
        /// <c>oObj:GetFoo():GetBar()</c> — to a type name.
        /// Returns <c>null</c> when resolution fails.
        /// </summary>
        /// <param name="dbService">
        /// Optional database service used as a fallback for assembly-level callables.
        /// Pass <c>null</c> to skip assembly lookup (e.g. when DB is unavailable).
        /// </param>
        public static string? Resolve(
            XSharpParserRuleContext tree,
            Position cursor,
            string rawIdentifier,
            XSharpWorkspaceIndex workspaceIndex,
            XSharpDatabaseService? dbService = null)
        {
            if (string.IsNullOrEmpty(rawIdentifier)) return null;

            var segments = SplitChain(rawIdentifier);
            if (segments.Count == 0) return null;

            string firstName = SegmentName(segments[0]);
            if (string.IsNullOrEmpty(firstName)) return null;

            string? currentType = ResolveIdentifier(
                tree, cursor, firstName, workspaceIndex, dbService);
            if (currentType == null) return null;

            // Walk the remaining chain segments as members of the progressively
            // resolved type — each segment's declared return type feeds the next.
            for (int i = 1; i < segments.Count; i++)
            {
                string memberName = SegmentName(segments[i]);
                if (string.IsNullOrEmpty(memberName)) return null;

                currentType = ResolveMemberType(currentType, memberName, workspaceIndex, dbService);
                if (currentType == null) return null;
            }

            return currentType;
        }

        /// <summary>
        /// Resolves a single identifier — the first segment of a member-access
        /// chain, or the whole expression when it is not a chain — to a type name.
        /// </summary>
        private static string? ResolveIdentifier(
            XSharpParserRuleContext tree,
            Position cursor,
            string rawIdentifier,
            XSharpWorkspaceIndex workspaceIndex,
            XSharpDatabaseService? dbService)
        {
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
                var localType = FindLocalType(funcCtx, cursor, rawIdentifier, workspaceIndex, dbService, tree);
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
                if (!string.IsNullOrEmpty(sym.ReturnType))
                    return CleanTypeName(sym.ReturnType);
            }

            // 6. Assembly-level callable fallback — covers functions from referenced
            //    assemblies that are not in the workspace index.
            if (dbService != null)
            {
                var assemblyOverloads = dbService.FindAssemblyOverloads(rawIdentifier);
                if (assemblyOverloads.Count > 0)
                {
                    var returnType = CleanTypeName(assemblyOverloads[0].ReturnType);
                    if (returnType != null) return returnType;
                }

                // Also check as a type name in referenced assemblies.
                var assemblyType = dbService.FindAssemblyExact(rawIdentifier);
                if (assemblyType != null && IsTypeKind(assemblyType.Kind))
                    return rawIdentifier;
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
        // Hover: local variable / parameter info
        // ====================================================================

        /// <summary>
        /// Looks for a LOCAL variable declaration or function/method parameter named
        /// <paramref name="identifier"/> that is in scope at <paramref name="cursor"/>.
        /// Returns a <see cref="LocalVarHoverInfo"/> when found, or <c>null</c> when
        /// no matching declaration exists in the current scope.
        /// <para>
        /// Unlike the completion-path <c>FindLocalType</c>, this method includes the
        /// declaration line itself (<c>declLine &lt;= cursor.Line</c>) so that hovering
        /// directly on the variable name inside its own declaration still works.
        /// </para>
        /// </summary>
        public static LocalVarHoverInfo? FindLocalVarHover(
            XSharpParserRuleContext  tree,
            Position                 cursor,
            string                   identifier,
            XSharpWorkspaceIndex     workspaceIndex,
            XSharpDatabaseService?   dbService = null)
        {
            FindEnclosingScope(tree, cursor, out var funcCtx, out var signature, out _);

            // Check parameters first — they're always in scope inside the body.
            if (signature?.ParamList?._Params != null)
            {
                foreach (var p in signature.ParamList._Params)
                {
                    if (p.Id == null) continue;
                    if (!string.Equals(p.Id.GetText(), identifier,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    return new LocalVarHoverInfo(
                        identifier,
                        ExplicitType: CleanTypeName(p.Type?.GetText()),
                        IsVar:    false,
                        IsParam:  true,
                        InferredType: null);
                }
            }

            if (funcCtx == null) return null;
            return WalkForLocalVarInfo(funcCtx, cursor, identifier, workspaceIndex, dbService, tree);
        }

        private static LocalVarHoverInfo? WalkForLocalVarInfo(
            IParseTree             node,
            Position               cursor,
            string                 identifier,
            XSharpWorkspaceIndex   workspaceIndex,
            XSharpDatabaseService? dbService,
            XSharpParserRuleContext tree)
        {
            if (node is not XSharpParserRuleContext ctx) return null;

            int declLine = Math.Max(0, ctx.Start.Line - 1);

            // LOCAL foo AS SomeType  /  LOCAL foo := expr
            if (ctx is XSharpParser.CommonLocalDeclContext decl
                && declLine <= cursor.Line      // <= so hovering on the declaration works
                && decl._LocalVars != null)
            {
                foreach (var lv in decl._LocalVars)
                {
                    if (lv.Id == null) continue;
                    if (!string.Equals(lv.Id.GetText(), identifier,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    var explicitType = CleanTypeName(lv.DataType?.GetText());
                    if (explicitType != null)
                        return new LocalVarHoverInfo(identifier, explicitType,
                            IsVar: false, IsParam: false, InferredType: null);

                    // No AS clause — try to infer from the initializer.
                    string? inferred = lv.Expression != null
                        ? InferTypeFromExpression(lv.Expression, workspaceIndex, dbService, tree, cursor)
                        : null;
                    return new LocalVarHoverInfo(identifier, ExplicitType: null,
                        IsVar: true, IsParam: false, InferredType: inferred);
                }
            }

            // VAR foo := expr  /  LOCAL IMPLIED foo := expr
            if (ctx is XSharpParser.VarLocalDeclContext varDecl
                && declLine <= cursor.Line
                && varDecl._ImpliedVars != null)
            {
                foreach (var iv in varDecl._ImpliedVars)
                {
                    if (iv.Id == null) continue;
                    if (!string.Equals(iv.Id.GetText(), identifier,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    string? inferred = iv.Expression != null
                        ? InferTypeFromExpression(iv.Expression, workspaceIndex, dbService, tree, cursor)
                        : null;
                    return new LocalVarHoverInfo(identifier, ExplicitType: null,
                        IsVar: true, IsParam: false, InferredType: inferred);
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                var result = WalkForLocalVarInfo(
                    node.GetChild(i), cursor, identifier, workspaceIndex, dbService, tree);
                if (result != null) return result;
            }

            return null;
        }

        // ====================================================================
        // LOCAL variable search
        // ====================================================================

        private static string? FindLocalType(
            XSharpParserRuleContext funcCtx,
            Position cursor,
            string identifier,
            XSharpWorkspaceIndex workspaceIndex,
            XSharpDatabaseService? dbService,
            XSharpParserRuleContext tree)
        {
            return WalkForLocal(funcCtx, cursor, identifier, workspaceIndex, dbService, tree);
        }

        private static string? WalkForLocal(
            IParseTree node,
            Position cursor,
            string identifier,
            XSharpWorkspaceIndex workspaceIndex,
            XSharpDatabaseService? dbService,
            XSharpParserRuleContext tree)
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
                        var inferred = InferTypeFromExpression(
                            lv.Expression, workspaceIndex, dbService, tree, cursor);
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
                        var inferred = InferTypeFromExpression(
                            iv.Expression, workspaceIndex, dbService, tree, cursor);
                        if (inferred != null) return inferred;
                    }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                var result = WalkForLocal(node.GetChild(i), cursor, identifier, workspaceIndex, dbService, tree);
                if (result != null) return result;
            }

            return null;
        }

        /// <summary>
        /// Walks an initializer expression subtree looking for:
        /// <list type="bullet">
        ///   <item><c>CtorCallContext</c> — <c>SomeClass{}</c> or <c>SomeClass()</c>;
        ///         the type is read directly from <c>Type</c>.</item>
        ///   <item><c>MethodCallContext</c> — <c>GetFoo()</c> or a deeper chain such
        ///         as <c>SELF:GetFoo():GetBar()</c>; resolved segment by segment via
        ///         <see cref="ResolveCallChainReturnType"/>.</item>
        ///   <item>A bare literal (string, numeric, date, logic, …) — its XSharp
        ///         type name is read directly from the token type.</item>
        /// </list>
        /// Returns <c>null</c> when the expression cannot be resolved.
        /// <para>
        /// Deliberately does <b>not</b> resolve plain identifiers by re-entering
        /// local-variable lookup — doing so risks infinite recursion for
        /// self-referential initializers (<c>LOCAL x := x:Foo()</c> shadowing an
        /// outer <c>x</c>) since resolution always runs against the original
        /// completion cursor, not the declaration's own position.
        /// </para>
        /// </summary>
        private static string? InferTypeFromExpression(
            IParseTree node,
            XSharpWorkspaceIndex workspaceIndex,
            XSharpDatabaseService? dbService,
            XSharpParserRuleContext tree,
            Position cursor)
        {
            if (node is XSharpParser.CtorCallContext ctor)
                return CleanTypeName(ctor.Type?.GetText());

            if (node is XSharpParser.MethodCallContext mc)
                return ResolveCallChainReturnType(
                    mc.Expr?.GetText() ?? string.Empty, tree, cursor, workspaceIndex, dbService);

            if (node is ITerminalNode term)
                return LiteralTypeName(term.Symbol?.Type ?? -1);

            // Recurse into child nodes (e.g. PrimaryExpressionContext wrapper).
            for (int i = 0; i < node.ChildCount; i++)
            {
                var result = InferTypeFromExpression(node.GetChild(i), workspaceIndex, dbService, tree, cursor);
                if (result != null) return result;
            }

            return null;
        }

        /// <summary>
        /// Resolves the return type of a call chain such as <c>GetFoo</c>,
        /// <c>SELF:GetFoo</c>, or <c>oObj:GetFoo():GetBar</c> — the receiver
        /// expression of a <see cref="XSharpParser.MethodCallContext"/> (i.e. the
        /// callee, without the final argument list).
        /// <para>
        /// The first segment is resolved via <see cref="ResolveChainReceiver"/>
        /// (SELF/SUPER, known type names, or a callable's declared return type —
        /// all flat lookups that cannot recurse into local-variable inference);
        /// each following segment is looked up as a member of the
        /// progressively-resolved type via <see cref="ResolveMemberType"/>.
        /// </para>
        /// </summary>
        private static string? ResolveCallChainReturnType(
            string chain,
            XSharpParserRuleContext tree,
            Position cursor,
            XSharpWorkspaceIndex workspaceIndex,
            XSharpDatabaseService? dbService)
        {
            if (string.IsNullOrEmpty(chain)) return null;

            var segments = SplitChain(chain);
            if (segments.Count == 0) return null;

            string firstName = SegmentName(segments[0]);
            if (string.IsNullOrEmpty(firstName)) return null;

            string? currentType = ResolveChainReceiver(firstName, tree, cursor, workspaceIndex);
            if (currentType == null) return null;

            for (int i = 1; i < segments.Count; i++)
            {
                string memberName = SegmentName(segments[i]);
                if (string.IsNullOrEmpty(memberName)) return null;

                currentType = ResolveMemberType(currentType, memberName, workspaceIndex, dbService);
                if (currentType == null) return null;
            }

            return currentType;
        }

        /// <summary>
        /// Resolves the first segment of a call chain found inside an
        /// initializer expression: <c>SELF</c>/<c>SUPER</c>, a known type name
        /// (static reference), or a callable's declared return type.
        /// Uses only flat <see cref="XSharpWorkspaceIndex.FindExact"/> lookups —
        /// never re-enters local-variable resolution (see
        /// <see cref="InferTypeFromExpression"/> for why).
        /// </summary>
        private static string? ResolveChainReceiver(
            string name,
            XSharpParserRuleContext tree,
            Position cursor,
            XSharpWorkspaceIndex workspaceIndex)
        {
            if (string.Equals(name, "SELF", StringComparison.OrdinalIgnoreCase))
                return FindEnclosingClassName(tree, cursor);

            if (string.Equals(name, "SUPER", StringComparison.OrdinalIgnoreCase))
            {
                var enclosing = FindEnclosingClassName(tree, cursor);
                if (enclosing == null) return null;
                var classSym = workspaceIndex.FindExact(enclosing);
                return classSym?.InheritsFrom is { Length: > 0 } parent
                    ? CleanTypeName(parent)
                    : null;
            }

            var sym = workspaceIndex.FindExact(name);
            if (sym == null) return null;

            return IsTypeKind(sym.Kind) ? name : CleanTypeName(sym.ReturnType);
        }

        /// <summary>
        /// Maps a literal token's lexer type to its XSharp type-keyword name
        /// (e.g. <c>STRING_CONST</c> → <c>"STRING"</c>) so it can feed
        /// <see cref="XSharpWorkspaceIndex.GetMembersOf"/> /
        /// <see cref="XSharpDatabaseService.FindAssemblyMembersOf"/> for member
        /// completion on bare-literal initializers (<c>VAR s := "abc"</c>).
        /// Returns <c>null</c> for non-literal token types.
        /// </summary>
        private static string? LiteralTypeName(int tokenType) => tokenType switch
        {
            XSharpLexer.STRING_CONST
                or XSharpLexer.ESCAPED_STRING_CONST
                or XSharpLexer.INTERPOLATED_STRING_CONST
                or XSharpLexer.TEXT_STRING_CONST
                or XSharpLexer.BRACKETED_STRING_CONST
                or XSharpLexer.INCOMPLETE_STRING_CONST => "STRING",
            XSharpLexer.CHAR_CONST     => "CHAR",
            XSharpLexer.SYMBOL_CONST   => "SYMBOL",
            XSharpLexer.INT_CONST
                or XSharpLexer.HEX_CONST
                or XSharpLexer.BIN_CONST
                or XSharpLexer.BINARY_CONST => "INT",
            XSharpLexer.REAL_CONST     => "REAL8",
            XSharpLexer.DATE_CONST     => "DATE",
            XSharpLexer.DATETIME_CONST => "DATETIME",
            XSharpLexer.TRUE_CONST
                or XSharpLexer.FALSE_CONST => "LOGIC",
            _ => null,
        };

        // ====================================================================
        // Chain walking  (oObj:GetFoo():GetBar() → resolve segment by segment)
        // ====================================================================

        /// <summary>
        /// Splits a member-access chain into its <c>:</c>/<c>.</c>-separated
        /// segments, ignoring separators that appear inside balanced
        /// <c>()</c>/<c>[]</c> or string/char literals (e.g. call arguments).
        /// </summary>
        private static List<string> SplitChain(string chain)
        {
            var segments = new List<string>();
            int depth = 0;
            int start = 0;
            bool inString = false;
            char quote = '\0';

            for (int i = 0; i < chain.Length; i++)
            {
                char c = chain[i];
                if (inString)
                {
                    if (c == quote) inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                    case '\'':
                        inString = true;
                        quote    = c;
                        break;
                    case '(':
                    case '[':
                        depth++;
                        break;
                    case ')':
                    case ']':
                        depth--;
                        break;
                    case ':':
                    case '.':
                        if (depth == 0)
                        {
                            segments.Add(chain[start..i]);
                            start = i + 1;
                        }
                        break;
                }
            }

            segments.Add(chain[start..]);
            return segments;
        }

        /// <summary>
        /// Extracts the bare name from a chain segment, stripping a trailing
        /// call's argument list: <c>GetFoo(x, y)</c> → <c>GetFoo</c>.
        /// </summary>
        private static string SegmentName(string segment)
        {
            segment = segment.Trim();
            int paren = segment.IndexOf('(');
            return (paren > 0 ? segment[..paren] : segment).Trim();
        }

        /// <summary>
        /// Looks up <paramref name="memberName"/> as a member of
        /// <paramref name="typeName"/> — workspace index first, then assembly
        /// reflection — and returns its declared return type, cleaned for use
        /// as the next chain segment's receiver type.
        /// </summary>
        private static string? ResolveMemberType(
            string typeName,
            string memberName,
            XSharpWorkspaceIndex workspaceIndex,
            XSharpDatabaseService? dbService)
        {
            foreach (var member in workspaceIndex.GetMembersOf(typeName))
            {
                if (!string.Equals(member.Name, memberName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var t = CleanTypeName(member.ReturnType);
                if (t != null) return t;
            }

            if (dbService != null)
            {
                foreach (var member in dbService.FindAssemblyMembersOf(typeName))
                {
                    if (!string.Equals(member.Name, memberName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var t = CleanTypeName(member.ReturnType);
                    if (t != null) return t;
                }
            }

            return null;
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
