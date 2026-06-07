using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree.Tree;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Shared parse-tree scope helpers used by rename and code-action handlers.
    /// <para>
    /// All methods are stateless and thread-safe.
    /// </para>
    /// </summary>
    public static class XSharpScopeHelper
    {
        // ====================================================================
        // Public API
        // ====================================================================

        /// <summary>
        /// Determines whether <paramref name="name"/> is declared as a
        /// scope-limited symbol inside the function/method that contains
        /// <paramref name="pos"/>.
        /// <para>
        /// Scope-limited symbols are:
        /// <list type="bullet">
        ///   <item><c>LOCAL name AS Type</c> — explicitly typed local variable.</item>
        ///   <item><c>VAR name := expr</c> / <c>LOCAL IMPLIED</c> — implicitly typed local.</item>
        ///   <item>Parameters in the function/method signature.</item>
        ///   <item><c>PARAMETERS x, y, z</c> — Clipper-style positional parameters.</item>
        ///   <item><c>MEMVAR x</c> / <c>PRIVATE x</c> — Clipper-style private memory variable.</item>
        /// </list>
        /// <c>PUBLIC x</c> (MEMVAR with PUBLIC modifier) is project-wide and returns <c>false</c>.
        /// </para>
        /// </summary>
        /// <param name="tree">Root of the parse tree.</param>
        /// <param name="pos">Cursor position (0-based LSP convention).</param>
        /// <param name="name">Identifier to test.</param>
        /// <param name="scopeStartLine">
        /// When <c>true</c> is returned: the 0-based start line of the
        /// enclosing function/method body (inclusive).
        /// </param>
        /// <param name="scopeEndLine">
        /// When <c>true</c> is returned: the 0-based end line of the
        /// enclosing function/method body (inclusive).
        /// </param>
        /// <returns>
        /// <c>true</c> when the identifier is definitively scope-limited;
        /// <c>false</c> when it is a global/type/member symbol or the
        /// enclosing function cannot be determined.
        /// </returns>
        public static bool IsLocalOrParameter(
            IParseTree tree,
            Position   pos,
            string     name,
            out int    scopeStartLine,
            out int    scopeEndLine)
        {
            scopeStartLine = 0;
            scopeEndLine   = int.MaxValue;

            FindEnclosingFunction(tree, pos,
                out var funcCtx, out var sig,
                out int startLine, out int endLine);

            if (funcCtx == null)
                return false;

            scopeStartLine = startLine;
            scopeEndLine   = endLine;

            // 1. Signature parameters (FUNCTION Foo(x AS INT, ...))
            if (CollectParameterNames(sig).Contains(name))
                return true;

            // 2. LOCAL / VAR declarations
            if (CollectLocalsInRange(funcCtx, startLine, endLine).ContainsKey(name))
                return true;

            // 3. PARAMETERS x, y, z  (Clipper-style positional params)
            if (CollectClipperParameters(funcCtx, startLine, endLine).Contains(name))
                return true;

            // 4. MEMVAR / PRIVATE x  (Clipper-style private memory variable)
            //    PUBLIC x is project-wide → not scope-limited.
            if (CollectPrivateMemvars(funcCtx, startLine, endLine).Contains(name))
                return true;

            return false;
        }

        /// <summary>
        /// Finds the innermost function/method/procedure/constructor/destructor
        /// whose range contains <paramref name="pos"/> and returns its parse
        /// tree node, signature, and 0-based line range.
        /// </summary>
        public static void FindEnclosingFunction(
            IParseTree                          tree,
            Position                            pos,
            out XSharpParserRuleContext?         funcCtx,
            out XSharpParser.SignatureContext?   sig,
            out int                             startLine,
            out int                             endLine)
        {
            funcCtx   = null;
            sig       = null;
            startLine = 0;
            endLine   = int.MaxValue;

            WalkForFunc(tree, pos, ref funcCtx, ref sig);

            if (funcCtx != null)
            {
                startLine = funcCtx.Start != null
                    ? Math.Max(0, funcCtx.Start.Line - 1) : 0;
                endLine   = funcCtx.Stop  != null
                    ? Math.Max(0, funcCtx.Stop.Line  - 1) : int.MaxValue;
            }
        }

        /// <summary>
        /// Returns the names of all parameters declared in
        /// <paramref name="sig"/>, case-insensitively.
        /// </summary>
        public static HashSet<string> CollectParameterNames(
            XSharpParser.SignatureContext? sig)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sig?.ParamList?._Params == null) return result;
            foreach (var p in sig.ParamList._Params)
                if (p.Id != null)
                    result.Add(p.Id.GetText());
            return result;
        }

        /// <summary>
        /// Returns the name → declared-type map for every
        /// <c>LOCAL</c> / <c>VAR</c> declaration whose source line falls within
        /// [<paramref name="fromLine"/>, <paramref name="toLine"/>] (0-based,
        /// inclusive) inside <paramref name="root"/>.
        /// </summary>
        public static Dictionary<string, string?> CollectLocalsInRange(
            XSharpParserRuleContext? root,
            int                     fromLine,
            int                     toLine)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return result;
            WalkLocals(root, fromLine, toLine, result);
            return result;
        }

        /// <summary>
        /// Returns the names of all Clipper-style <c>PARAMETERS</c> declared
        /// within [<paramref name="fromLine"/>, <paramref name="toLine"/>]
        /// inside <paramref name="root"/>.
        /// </summary>
        public static HashSet<string> CollectClipperParameters(
            XSharpParserRuleContext? root,
            int                     fromLine,
            int                     toLine)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return result;
            WalkClipperParams(root, fromLine, toLine, result);
            return result;
        }

        /// <summary>
        /// Returns the names of all <c>MEMVAR</c> / <c>PRIVATE</c> variables
        /// declared within [<paramref name="fromLine"/>, <paramref name="toLine"/>]
        /// inside <paramref name="root"/>.
        /// Variables declared with <c>PUBLIC</c> are excluded (project-wide scope).
        /// </summary>
        public static HashSet<string> CollectPrivateMemvars(
            XSharpParserRuleContext? root,
            int                     fromLine,
            int                     toLine)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return result;
            WalkMemvars(root, fromLine, toLine, result);
            return result;
        }

        // ====================================================================
        // Private walkers
        // ====================================================================

        private static void WalkForFunc(
            IParseTree                         node,
            Position                           pos,
            ref XSharpParserRuleContext?        funcCtx,
            ref XSharpParser.SignatureContext?  sig)
        {
            if (node is not XSharpParserRuleContext ctx) return;

            int startLine = ctx.Start != null ? Math.Max(0, ctx.Start.Line - 1) : 0;
            int stopLine  = ctx.Stop  != null ? Math.Max(0, ctx.Stop.Line  - 1) : startLine;
            if (pos.Line < startLine || pos.Line > stopLine) return;

            switch (ctx)
            {
                case XSharpParser.FuncprocContext fp when fp.Sig != null:
                    funcCtx = fp; sig = fp.Sig; break;
                case XSharpParser.MethodContext m when m.Sig != null:
                    funcCtx = m; sig = m.Sig; break;
                case XSharpParser.ConstructorContext co:
                    funcCtx = co; sig = null; break;
                case XSharpParser.DestructorContext de:
                    funcCtx = de; sig = null; break;
            }

            for (int i = 0; i < node.ChildCount; i++)
                WalkForFunc(node.GetChild(i), pos, ref funcCtx, ref sig);
        }

        private static void WalkLocals(
            IParseTree                   node,
            int                          fromLine,
            int                          toLine,
            Dictionary<string, string?>  result)
        {
            if (node is not XSharpParserRuleContext ctx) return;

            if (ctx is XSharpParser.CommonLocalDeclContext decl
                && decl._LocalVars != null)
            {
                int dl = Math.Max(0, ctx.Start.Line - 1);
                if (dl >= fromLine && dl <= toLine)
                    foreach (var lv in decl._LocalVars)
                        if (lv.Id != null)
                            result.TryAdd(lv.Id.GetText(), lv.DataType?.GetText());
            }

            if (ctx is XSharpParser.VarLocalDeclContext varDecl
                && varDecl._ImpliedVars != null)
            {
                int dl = Math.Max(0, ctx.Start.Line - 1);
                if (dl >= fromLine && dl <= toLine)
                    foreach (var iv in varDecl._ImpliedVars)
                        if (iv.Id != null)
                            result.TryAdd(iv.Id.GetText(), null);
            }

            for (int i = 0; i < node.ChildCount; i++)
                WalkLocals(node.GetChild(i), fromLine, toLine, result);
        }

        private static void WalkClipperParams(
            IParseTree          node,
            int                 fromLine,
            int                 toLine,
            HashSet<string>     result)
        {
            if (node is not XSharpParserRuleContext ctx) return;

            // MemvardeclContext with PARAMETERS() token set = Clipper PARAMETERS stmt
            if (ctx is XSharpParser.MemvardeclContext pd
                && pd.PARAMETERS() != null)
            {
                int dl = Math.Max(0, ctx.Start.Line - 1);
                if (dl >= fromLine && dl <= toLine)
                {
                    foreach (var mv in pd.memvar())
                    {
                        var nameText = mv.varidentifierName()?.GetText();
                        if (!string.IsNullOrEmpty(nameText))
                            result.Add(nameText);
                    }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
                WalkClipperParams(node.GetChild(i), fromLine, toLine, result);
        }

        private static void WalkMemvars(
            IParseTree      node,
            int             fromLine,
            int             toLine,
            HashSet<string> result)
        {
            if (node is not XSharpParserRuleContext ctx) return;

            // MemvardeclContext with MEMVAR() or PRIVATE() token but NOT PUBLIC()
            if (ctx is XSharpParser.MemvardeclContext md
                && (md.MEMVAR() != null || md.PRIVATE() != null)
                && md.PUBLIC() == null)
            {
                int dl = Math.Max(0, ctx.Start.Line - 1);
                if (dl >= fromLine && dl <= toLine)
                {
                    foreach (var mv in md.memvar())
                    {
                        var nameText = mv.varidentifierName()?.GetText();
                        if (!string.IsNullOrEmpty(nameText))
                            result.Add(nameText);
                    }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
                WalkMemvars(node.GetChild(i), fromLine, toLine, result);
        }
    }
}
