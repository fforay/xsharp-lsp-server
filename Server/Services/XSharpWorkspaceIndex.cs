using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using XSharpLanguageServer.Models;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// In-process workspace symbol index built directly from source files.
    /// <para>
    /// This is Tier-1 of the two-tier symbol resolution strategy.  All handlers
    /// query this index first; the <see cref="XSharpDatabaseService"/> is used
    /// only as a fallback for symbols from referenced assemblies
    /// (ReferencedTypes / ReferencedGlobals) that cannot be derived from source.
    /// </para>
    /// <para>
    /// The index is populated at startup by a background scanner
    /// (<c>XSharpInitializedHandler</c>) and kept current by
    /// <c>XSharpTextDocumentSyncHandler</c> on every <c>didSave</c>.
    /// External file changes are handled via <c>workspace/didChangeWatchedFiles</c>.
    /// </para>
    /// <para>
    /// Thread safety: a <see cref="ReaderWriterLockSlim"/> guards all dictionary
    /// access.  Reads are concurrent; writes (UpdateFile / RemoveFile) are
    /// exclusive but brief.
    /// </para>
    /// </summary>
    public sealed class XSharpWorkspaceIndex : IDisposable
    {
        private readonly ILogger<XSharpWorkspaceIndex> _logger;

        // Primary index: UPPER-CASE name → all declarations with that name.
        private readonly Dictionary<string, List<WorkspaceSymbol>> _byName
            = new(StringComparer.Ordinal);

        // Member index: UPPER-CASE declaring type name → members of that type.
        private readonly Dictionary<string, List<WorkspaceSymbol>> _byTypeName
            = new(StringComparer.Ordinal);

        // File index: normalised file path → symbols declared in that file.
        // Used to remove stale entries when a file is re-indexed or deleted.
        private readonly Dictionary<string, List<WorkspaceSymbol>> _byFile
            = new(StringComparer.OrdinalIgnoreCase);

        // ── Token (usage) index ──────────────────────────────────────────────
        // Inverted: UPPER-CASE identifier text → all locations across all files.
        private readonly Dictionary<string, List<IdentifierLocation>> _tokensByName
            = new(StringComparer.Ordinal);

        // Per-file: normalised path → every identifier token in that file.
        // Used to remove stale entries on update/delete.
        private readonly Dictionary<string, List<IdentifierLocation>> _tokensByFile
            = new(StringComparer.OrdinalIgnoreCase);

        private readonly ReaderWriterLockSlim _rwLock
            = new(LockRecursionPolicy.NoRecursion);

        public XSharpWorkspaceIndex(ILogger<XSharpWorkspaceIndex> logger)
        {
            _logger = logger;
        }

        // ====================================================================
        // Mutation
        // ====================================================================

        /// <summary>
        /// Replaces all indexed symbols for <paramref name="filePath"/> with the
        /// provided <paramref name="symbols"/> list.
        /// <para>
        /// Called by <c>XSharpTextDocumentSyncHandler</c> after <c>didSave</c> and
        /// by the startup background scanner after parsing each file.
        /// </para>
        /// </summary>
        public void UpdateFile(string filePath, IEnumerable<WorkspaceSymbol> symbols)
        {
            var normalized = NormalizePath(filePath);
            var list = symbols.ToList();

            _rwLock.EnterWriteLock();
            try
            {
                RemoveFileCore(normalized);
                AddSymbolsCore(normalized, list);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            _logger.LogDebug(
                "WorkspaceIndex: indexed {Count} symbol(s) from {File}",
                list.Count, System.IO.Path.GetFileName(filePath));
        }

        /// <summary>
        /// Removes all indexed symbols and identifier tokens that originated from
        /// <paramref name="filePath"/>. Called when a file is deleted or renamed.
        /// </summary>
        public void RemoveFile(string filePath)
        {
            var normalized = NormalizePath(filePath);

            _rwLock.EnterWriteLock();
            try
            {
                RemoveFileCore(normalized);
                RemoveTokensCore(normalized);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Replaces all indexed identifier tokens for <paramref name="filePath"/>
        /// with the provided <paramref name="tokens"/> list.
        /// <para>
        /// Called by <c>XSharpWorkspaceScanner</c>, <c>XSharpTextDocumentSyncHandler</c>
        /// on <c>didSave</c>, and <c>XSharpDidChangeWatchedFilesHandler</c> after each
        /// file parse so that <see cref="FindTokenLocations"/> covers all project files.
        /// </para>
        /// </summary>
        public void UpdateFileTokens(string filePath, IEnumerable<IdentifierLocation> tokens)
        {
            var normalized = NormalizePath(filePath);
            var list = tokens.ToList();

            _rwLock.EnterWriteLock();
            try
            {
                RemoveTokensCore(normalized);
                AddTokensCore(normalized, list);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        // ====================================================================
        // Token queries
        // ====================================================================

        /// <summary>
        /// Returns every location in the project where <paramref name="name"/> appears
        /// as an identifier token, across all indexed files.
        /// <para>
        /// Used by <c>XSharpReferencesHandler</c> to find usages in closed files.
        /// Open-document results (which may contain unsaved edits) should be merged
        /// by the caller, with open-file results taking precedence.
        /// </para>
        /// </summary>
        public IReadOnlyList<IdentifierLocation> FindTokenLocations(string name)
        {
            if (string.IsNullOrEmpty(name)) return Array.Empty<IdentifierLocation>();

            var upper = name.ToUpperInvariant();

            _rwLock.EnterReadLock();
            try
            {
                if (_tokensByName.TryGetValue(upper, out var locations))
                    return locations.ToList();
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            return Array.Empty<IdentifierLocation>();
        }

        // ====================================================================
        // Queries  (mirror XSharpDatabaseService API for mechanical handler swaps)
        // ====================================================================

        /// <summary>
        /// Returns all types and global members whose name starts with
        /// <paramref name="prefix"/> (case-insensitive).
        /// Used for cross-file completion.
        /// </summary>
        public IReadOnlyList<WorkspaceSymbol> FindByPrefix(string prefix, int maxResults = 100)
        {
            if (string.IsNullOrEmpty(prefix)) return Array.Empty<WorkspaceSymbol>();

            var upper = prefix.ToUpperInvariant();
            var results = new List<WorkspaceSymbol>();

            _rwLock.EnterReadLock();
            try
            {
                foreach (var kvp in _byName)
                {
                    if (!kvp.Key.StartsWith(upper, StringComparison.Ordinal)) continue;
                    foreach (var sym in kvp.Value)
                    {
                        // Only top-level symbols (no TypeName) for general prefix search.
                        if (sym.TypeName == null)
                            results.Add(sym);
                        if (results.Count >= maxResults) goto done;
                    }
                }
                done:;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            return results;
        }

        /// <summary>
        /// Returns all members of the type whose name exactly matches
        /// <paramref name="typeName"/> (case-insensitive).
        /// Used for member completion after <c>.</c> or <c>:</c>.
        /// </summary>
        public IReadOnlyList<WorkspaceSymbol> GetMembersOf(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return Array.Empty<WorkspaceSymbol>();

            var upper = typeName.ToUpperInvariant();

            _rwLock.EnterReadLock();
            try
            {
                if (_byTypeName.TryGetValue(upper, out var members))
                    return members.ToList();
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            return Array.Empty<WorkspaceSymbol>();
        }

        /// <summary>
        /// Looks up a symbol by exact name.
        /// Returns the first match, preferring the current file when possible.
        /// Used for hover and go-to-definition.
        /// </summary>
        public WorkspaceSymbol? FindExact(string name, string? currentFile = null)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var upper = name.ToUpperInvariant();

            _rwLock.EnterReadLock();
            try
            {
                if (!_byName.TryGetValue(upper, out var bucket) || bucket.Count == 0)
                    return null;

                if (currentFile != null)
                {
                    var norm = NormalizePath(currentFile);
                    var local = bucket.FirstOrDefault(
                        s => NormalizePath(s.FileName).Equals(norm, StringComparison.OrdinalIgnoreCase));
                    if (local != null) return local;
                }

                return bucket[0];
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns all overloads of a function or method by exact name.
        /// Used for signature help — multiple overloads → multiple signatures.
        /// </summary>
        public IReadOnlyList<WorkspaceSymbol> FindOverloads(string name)
        {
            if (string.IsNullOrEmpty(name)) return Array.Empty<WorkspaceSymbol>();

            var upper = name.ToUpperInvariant();

            _rwLock.EnterReadLock();
            try
            {
                if (_byName.TryGetValue(upper, out var bucket))
                    return bucket.ToList();
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            return Array.Empty<WorkspaceSymbol>();
        }

        /// <summary>
        /// Returns every declaration of <paramref name="name"/> across all project
        /// files — both types and members, all overloads.
        /// Used by find-references and rename to seed the declaration-site list.
        /// </summary>
        public IReadOnlyList<WorkspaceSymbol> FindAllByName(string name)
            => FindOverloads(name);   // same storage, no distinction needed

        // ====================================================================
        // Diagnostics
        // ====================================================================

        /// <summary>Returns the number of source files currently indexed.</summary>
        public int IndexedFileCount
        {
            get
            {
                _rwLock.EnterReadLock();
                try { return _byFile.Count; }
                finally { _rwLock.ExitReadLock(); }
            }
        }

        /// <summary>Returns the total number of symbols currently indexed.</summary>
        public int IndexedSymbolCount
        {
            get
            {
                _rwLock.EnterReadLock();
                try { return _byName.Values.Sum(b => b.Count); }
                finally { _rwLock.ExitReadLock(); }
            }
        }

        /// <summary>Returns the total number of identifier token locations indexed.</summary>
        public int IndexedTokenCount
        {
            get
            {
                _rwLock.EnterReadLock();
                try { return _tokensByName.Values.Sum(b => b.Count); }
                finally { _rwLock.ExitReadLock(); }
            }
        }

        // ====================================================================
        // Private helpers  (must be called with write lock held)
        // ====================================================================

        private void RemoveFileCore(string normalizedPath)
        {
            if (!_byFile.TryGetValue(normalizedPath, out var oldSymbols)) return;

            foreach (var sym in oldSymbols)
            {
                var nameKey = sym.Name.ToUpperInvariant();
                if (_byName.TryGetValue(nameKey, out var nameBucket))
                {
                    nameBucket.Remove(sym);
                    if (nameBucket.Count == 0)
                        _byName.Remove(nameKey);
                }

                if (sym.TypeName != null)
                {
                    var typeKey = sym.TypeName.ToUpperInvariant();
                    if (_byTypeName.TryGetValue(typeKey, out var typeBucket))
                    {
                        typeBucket.Remove(sym);
                        if (typeBucket.Count == 0)
                            _byTypeName.Remove(typeKey);
                    }
                }
            }

            _byFile.Remove(normalizedPath);
        }

        private void AddSymbolsCore(string normalizedPath, List<WorkspaceSymbol> symbols)
        {
            _byFile[normalizedPath] = symbols;

            foreach (var sym in symbols)
            {
                var nameKey = sym.Name.ToUpperInvariant();
                if (!_byName.TryGetValue(nameKey, out var nameBucket))
                {
                    nameBucket = new List<WorkspaceSymbol>();
                    _byName[nameKey] = nameBucket;
                }
                nameBucket.Add(sym);

                if (sym.TypeName != null)
                {
                    var typeKey = sym.TypeName.ToUpperInvariant();
                    if (!_byTypeName.TryGetValue(typeKey, out var typeBucket))
                    {
                        typeBucket = new List<WorkspaceSymbol>();
                        _byTypeName[typeKey] = typeBucket;
                    }
                    typeBucket.Add(sym);
                }
            }
        }

        private void RemoveTokensCore(string normalizedPath)
        {
            if (!_tokensByFile.TryGetValue(normalizedPath, out var oldTokens)) return;

            foreach (var tok in oldTokens)
            {
                var key = tok.Text.ToUpperInvariant();
                if (_tokensByName.TryGetValue(key, out var bucket))
                {
                    bucket.RemoveAll(t => NormalizePath(t.FilePath)
                        .Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
                    if (bucket.Count == 0)
                        _tokensByName.Remove(key);
                }
            }

            _tokensByFile.Remove(normalizedPath);
        }

        private void AddTokensCore(string normalizedPath, List<IdentifierLocation> tokens)
        {
            _tokensByFile[normalizedPath] = tokens;

            foreach (var tok in tokens)
            {
                var key = tok.Text.ToUpperInvariant();
                if (!_tokensByName.TryGetValue(key, out var bucket))
                {
                    bucket = new List<IdentifierLocation>();
                    _tokensByName[key] = bucket;
                }
                bucket.Add(tok);
            }
        }

        private static string NormalizePath(string path)
            => path.Replace('\\', '/').TrimEnd('/');

        public void Dispose() => _rwLock.Dispose();
    }
}
