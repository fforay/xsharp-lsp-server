using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using XSharpLanguageServer.Models;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Provides read-only access to the XSharp IntelliSense database
    /// (<c>X#Model.xsdb</c>) that the Visual Studio extension maintains.
    /// <para>
    /// The database is located automatically by walking up from the workspace
    /// root directory to find a <c>.sln</c> file, then resolving
    /// <c>.vs\&lt;solution-name&gt;\X#Model.xsdb</c>.
    /// </para>
    /// <para>
    /// All operations are read-only.  If the database cannot be found or
    /// opened, all query methods return empty results so the server degrades
    /// gracefully.
    /// </para>
    /// <para>
    /// A <see cref="FileSystemWatcher"/> monitors the <c>.vs\</c> subtree for
    /// <c>X#Model.xsdb</c> Created / Changed events so the server reconnects
    /// automatically when Visual Studio flushes a fresh copy of the database
    /// (typically every ~5 minutes) or when the file first appears after the
    /// server started.  A 2-second debounce timer prevents thrashing during a
    /// multi-step file write.
    /// </para>
    /// </summary>
    public sealed class XSharpDatabaseService : IDisposable
    {
        private readonly ILogger<XSharpDatabaseService> _logger;

        /// <summary>
        /// Live read-only connection, or <c>null</c> if the DB has not been
        /// found / could not be opened.
        /// </summary>
        private SqliteConnection? _connection;

        /// <summary>
        /// Full path of the database file currently in use, for logging.
        /// </summary>
        private string? _dbPath;

        /// <summary>Workspace root supplied on first <see cref="TryConnect"/> call.</summary>
        private string? _workspaceRoot;

        /// <summary>Watches the <c>.vs\</c> subtree for DB file changes.</summary>
        private FileSystemWatcher? _watcher;

        /// <summary>
        /// Debounce timer — fires the actual reconnect 2 s after the last
        /// filesystem event so we don't reconnect on every write during a
        /// multi-step flush.
        /// </summary>
        private Timer? _debounce;

        /// <summary>Lock protecting <see cref="_connection"/> and <see cref="_dbPath"/>.</summary>
        private readonly object _lock = new();

        private const int DebounceMs = 2_000;

        /// <summary>Initialises the service. Called by the DI container.</summary>
        public XSharpDatabaseService(ILogger<XSharpDatabaseService> logger)
        {
            _logger = logger;
        }

        // ====================================================================
        // Initialisation — called once the workspace root is known
        // ====================================================================

        /// <summary>
        /// Attempts to locate and open the <c>X#Model.xsdb</c> database that
        /// corresponds to the given <paramref name="workspaceRoot"/> directory,
        /// then installs a <see cref="FileSystemWatcher"/> on the <c>.vs\</c>
        /// subtree so the connection is refreshed automatically whenever VS
        /// flushes a new copy of the database.
        /// <para>
        /// Safe to call multiple times — the watcher is only installed once.
        /// If the DB file does not exist yet the watcher will pick it up when
        /// it appears.
        /// </para>
        /// </summary>
        public void TryConnect(string workspaceRoot)
        {
            lock (_lock)
            {
                // Remember root for reconnects triggered by the watcher.
                if (_workspaceRoot == null)
                {
                    _workspaceRoot = workspaceRoot;
                    StartWatcher(workspaceRoot);
                }

                ConnectCore(workspaceRoot);
            }
        }

        /// <summary>
        /// Closes the current connection (if any) and opens a fresh one.
        /// Called by the debounce timer after a filesystem change event.
        /// </summary>
        private void Reconnect()
        {
            lock (_lock)
            {
                CloseConnection();
                if (_workspaceRoot != null)
                    ConnectCore(_workspaceRoot);
            }
        }

        /// <summary>
        /// Core connect logic — locates the DB and opens a read-only connection.
        /// Must be called with <see cref="_lock"/> held.
        /// </summary>
        private void ConnectCore(string workspaceRoot)
        {
            if (_connection != null) return;   // already open

            string? dbPath = FindDatabase(workspaceRoot);
            if (dbPath == null)
            {
                _logger.LogWarning(
                    "X#Model.xsdb not found under {Root} — DB-backed features disabled",
                    workspaceRoot);
                return;
            }

            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode       = SqliteOpenMode.ReadOnly,
                    Cache      = SqliteCacheMode.Shared,
                };

                var conn = new SqliteConnection(builder.ToString());
                conn.Open();

                _connection = conn;
                _dbPath     = dbPath;

                _logger.LogInformation("Opened X#Model.xsdb at {Path}", dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open X#Model.xsdb at {Path}", dbPath);
            }
        }

        /// <summary>
        /// Closes and disposes the current connection.
        /// Must be called with <see cref="_lock"/> held.
        /// </summary>
        private void CloseConnection()
        {
            if (_connection == null) return;
            try { _connection.Dispose(); } catch { /* ignore */ }
            _connection = null;
            _dbPath     = null;
        }

        // ====================================================================
        // FileSystemWatcher
        // ====================================================================

        /// <summary>
        /// Installs a <see cref="FileSystemWatcher"/> on the <c>.vs\</c>
        /// subdirectory (created if it does not yet exist) to detect when
        /// <c>X#Model.xsdb</c> is created or changed.
        /// </summary>
        private void StartWatcher(string workspaceRoot)
        {
            // Walk up to find the solution directory (same logic as FindDatabase
            // but we want the directory even when the DB doesn't exist yet).
            string? vsDir = FindVsDir(workspaceRoot);
            if (vsDir == null)
            {
                _logger.LogDebug(
                    "No .vs directory found under {Root} — skipping FileSystemWatcher", workspaceRoot);
                return;
            }

            try
            {
                var w = new FileSystemWatcher(vsDir)
                {
                    Filter              = "X#Model.xsdb",
                    IncludeSubdirectories = true,
                    NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };

                w.Created += OnDbFileEvent;
                w.Changed += OnDbFileEvent;

                _watcher = w;
                _logger.LogInformation(
                    "FileSystemWatcher installed on {Dir} for X#Model.xsdb", vsDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not install FileSystemWatcher on {Dir}", vsDir);
            }
        }

        private void OnDbFileEvent(object sender, FileSystemEventArgs e)
        {
            _logger.LogDebug("DB file event: {Type} {Path}", e.ChangeType, e.FullPath);

            // Restart the debounce timer — actual reconnect fires 2 s after
            // the last event so we don't reconnect on every write during a flush.
            _debounce?.Dispose();
            _debounce = new Timer(
                _ => Reconnect(),
                null,
                dueTime: DebounceMs,
                period:  Timeout.Infinite);
        }

        /// <summary>Returns <c>true</c> if the database connection is open.</summary>
        public bool IsAvailable
        {
            get { lock (_lock) { return _connection != null; } }
        }

        // ====================================================================
        // Queries
        // ====================================================================

        /// <summary>
        /// Returns all types and global members whose name starts with
        /// <paramref name="prefix"/> (case-insensitive).
        /// Used for cross-file completion.
        /// </summary>
        public IReadOnlyList<DbSymbol> FindByPrefix(string prefix, int maxResults = 100)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(prefix))
                return Array.Empty<DbSymbol>();

            var results = new List<DbSymbol>();

            try
            {
                // Types
                const string typeSql = @"
                    SELECT t.Name, t.Kind, NULL AS ReturnType,
                           t.Sourcecode, t.XmlComments,
                           f.FileName, t.StartLine, t.StartColumn
                    FROM   ProjectTypes t
                    JOIN   Files f ON f.Id = t.idFile
                    WHERE  t.Name LIKE @prefix ESCAPE '\'
                    LIMIT  @max";

                using var typeCmd = new SqliteCommand(typeSql, conn);
                typeCmd.Parameters.AddWithValue("@prefix", EscapeLike(prefix) + "%");
                typeCmd.Parameters.AddWithValue("@max", maxResults);
                ReadSymbols(typeCmd, results);

                // Members (functions, procedures, globals — IdType IS NULL)
                const string memberSql = @"
                    SELECT m.Name, m.Kind, m.ReturnType,
                           m.Sourcecode, m.XmlComments,
                           f.FileName, m.StartLine, m.StartColumn
                    FROM   ProjectMembers m
                    JOIN   Files f ON f.Id = m.IdFile
                    WHERE  m.Name LIKE @prefix ESCAPE '\'
                      AND  m.IdType IS NULL
                    LIMIT  @max";

                using var memberCmd = new SqliteCommand(memberSql, conn);
                memberCmd.Parameters.AddWithValue("@prefix", EscapeLike(prefix) + "%");
                memberCmd.Parameters.AddWithValue("@max", maxResults);
                ReadSymbols(memberCmd, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindByPrefix failed for '{Prefix}'", prefix);
            }

            return results;
        }

        /// <summary>
        /// Returns all members of the type whose name exactly matches
        /// <paramref name="typeName"/> (case-insensitive).
        /// Used for member completion after <c>.</c> or <c>:</c>.
        /// </summary>
        public IReadOnlyList<DbSymbol> GetMembersOf(string typeName)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(typeName))
                return Array.Empty<DbSymbol>();

            var results = new List<DbSymbol>();

            try
            {
                const string sql = @"
                    SELECT m.Name, m.Kind, m.ReturnType,
                           m.Sourcecode, m.XmlComments,
                           f.FileName, m.StartLine, m.StartColumn
                    FROM   ProjectMembers m
                    JOIN   Files f ON f.Id = m.IdFile
                    WHERE  m.IdType = (
                               SELECT Id FROM Types
                               WHERE  Name = @typeName COLLATE NOCASE
                               LIMIT  1
                           )";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@typeName", typeName);
                ReadSymbols(cmd, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMembersOf failed for '{Type}'", typeName);
            }

            return results;
        }

        /// <summary>
        /// Looks up a symbol by exact name — searches both types and members.
        /// Returns the first match, preferring the current file when possible.
        /// Used for hover and go-to-definition.
        /// </summary>
        public DbSymbol? FindExact(string name, string? currentFile = null)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(name))
                return null;

            try
            {
                // 1. Project-level types
                const string typeSql = @"
                    SELECT t.Name, t.Kind, NULL AS ReturnType,
                           t.Sourcecode, t.XmlComments,
                           f.FileName, t.StartLine, t.StartColumn,
                           NULL AS TypeName
                    FROM   ProjectTypes t
                    JOIN   Files f ON f.Id = t.idFile
                    WHERE  t.Name = @name COLLATE NOCASE
                    ORDER  BY CASE WHEN f.FileName = @file THEN 0 ELSE 1 END
                    LIMIT  1";

                using var typeCmd = new SqliteCommand(typeSql, conn);
                typeCmd.Parameters.AddWithValue("@name", name);
                typeCmd.Parameters.AddWithValue("@file", currentFile ?? string.Empty);
                var typeResult = ReadSingleSymbol(typeCmd);
                if (typeResult != null) return typeResult;

                // 2. Project-level members — include declaring type name for the hover card
                const string memberSql = @"
                    SELECT m.Name, m.Kind, m.ReturnType,
                           m.Sourcecode, m.XmlComments,
                           f.FileName, m.StartLine, m.StartColumn,
                           tp.Name AS TypeName
                    FROM   ProjectMembers m
                    JOIN   Files f ON f.Id = m.IdFile
                    LEFT JOIN Types tp ON tp.Id = m.IdType
                    WHERE  m.Name = @name COLLATE NOCASE
                    ORDER  BY CASE WHEN f.FileName = @file THEN 0 ELSE 1 END
                    LIMIT  1";

                using var memberCmd = new SqliteCommand(memberSql, conn);
                memberCmd.Parameters.AddWithValue("@name", name);
                memberCmd.Parameters.AddWithValue("@file", currentFile ?? string.Empty);
                var memberResult = ReadSingleSymbol(memberCmd);
                if (memberResult != null) return memberResult;

                // 3. Assembly-level types (ReferencedTypes from referenced .NET assemblies)
                const string asmTypeSql = @"
                    SELECT rt.Name, rt.Kind, NULL AS ReturnType,
                           NULL AS Sourcecode, NULL AS XmlComments,
                           a.AssemblyFileName, 0 AS StartLine, 0 AS StartColumn,
                           NULL AS TypeName
                    FROM   ReferencedTypes rt
                    JOIN   Assemblies a ON a.Id = rt.idAssembly
                    WHERE  rt.Name = @name COLLATE NOCASE
                    LIMIT  1";

                using var asmTypeCmd = new SqliteCommand(asmTypeSql, conn);
                asmTypeCmd.Parameters.AddWithValue("@name", name);
                var asmTypeResult = ReadSingleSymbol(asmTypeCmd);
                if (asmTypeResult != null) return asmTypeResult;

                // 4. Assembly-level globals (ReferencedGlobals — functions/globals from assemblies)
                const string asmGlobalSql = @"
                    SELECT rg.Name, rg.Kind, rg.ReturnType,
                           rg.Sourcecode, NULL AS XmlComments,
                           a.AssemblyFileName, 0 AS StartLine, 0 AS StartColumn,
                           NULL AS TypeName
                    FROM   ReferencedGlobals rg
                    JOIN   Assemblies a ON a.Id = rg.idAssembly
                    WHERE  rg.Name = @name COLLATE NOCASE
                    LIMIT  1";

                using var asmGlobalCmd = new SqliteCommand(asmGlobalSql, conn);
                asmGlobalCmd.Parameters.AddWithValue("@name", name);
                return ReadSingleSymbol(asmGlobalCmd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindExact failed for '{Name}'", name);
                return null;
            }
        }

        /// <summary>
        /// Returns all overloads of a function or method by exact name.
        /// Used for signature help — multiple overloads → multiple signatures.
        /// </summary>
        public IReadOnlyList<DbSymbol> FindOverloads(string name)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(name))
                return Array.Empty<DbSymbol>();

            var results = new List<DbSymbol>();

            try
            {
                const string sql = @"
                    SELECT m.Name, m.Kind, m.ReturnType,
                           m.Sourcecode, m.XmlComments,
                           f.FileName, m.StartLine, m.StartColumn
                    FROM   ProjectMembers m
                    JOIN   Files f ON f.Id = m.IdFile
                    WHERE  m.Name = @name COLLATE NOCASE";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@name", name);
                ReadSymbols(cmd, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindOverloads failed for '{Name}'", name);
            }

            return results;
        }

        /// <summary>
        /// Returns every declaration of <paramref name="name"/> across all project
        /// files — both types and members, all overloads.
        /// Used by find-references to seed the result list with declaration sites.
        /// </summary>
        public IReadOnlyList<DbSymbol> FindAllByName(string name)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(name))
                return Array.Empty<DbSymbol>();

            var results = new List<DbSymbol>();

            try
            {
                const string typeSql = @"
                    SELECT t.Name, t.Kind, NULL AS ReturnType,
                           t.Sourcecode, t.XmlComments,
                           f.FileName, t.StartLine, t.StartColumn
                    FROM   ProjectTypes t
                    JOIN   Files f ON f.Id = t.idFile
                    WHERE  t.Name = @name COLLATE NOCASE";

                using var typeCmd = new SqliteCommand(typeSql, conn);
                typeCmd.Parameters.AddWithValue("@name", name);
                ReadSymbols(typeCmd, results);

                const string memberSql = @"
                    SELECT m.Name, m.Kind, m.ReturnType,
                           m.Sourcecode, m.XmlComments,
                           f.FileName, m.StartLine, m.StartColumn
                    FROM   ProjectMembers m
                    JOIN   Files f ON f.Id = m.IdFile
                    WHERE  m.Name = @name COLLATE NOCASE";

                using var memberCmd = new SqliteCommand(memberSql, conn);
                memberCmd.Parameters.AddWithValue("@name", name);
                ReadSymbols(memberCmd, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindAllByName failed for '{Name}'", name);
            }

            return results;
        }



        /// <summary>
        /// Walks up from <paramref name="startDir"/> looking for a <c>.sln</c> file.
        /// When found, resolves <c>.vs\&lt;name&gt;\X#Model.xsdb</c>.
        /// Returns <c>null</c> if nothing is found.
        /// </summary>
        private static string? FindDatabase(string startDir)
        {
            var dir = new DirectoryInfo(startDir);

            while (dir != null)
            {
                var slnFiles = dir.GetFiles("*.sln");
                foreach (var sln in slnFiles)
                {
                    string name   = Path.GetFileNameWithoutExtension(sln.Name);
                    string dbPath = Path.Combine(dir.FullName, ".vs", name, "X#Model.xsdb");
                    if (File.Exists(dbPath))
                        return dbPath;
                }

                dir = dir.Parent;
            }

            return null;
        }

        /// <summary>
        /// Walks up from <paramref name="startDir"/> looking for a <c>.vs\</c>
        /// subdirectory (which exists even when the DB hasn't been written yet).
        /// Returns the first <c>.vs</c> path found, or <c>null</c>.
        /// </summary>
        private static string? FindVsDir(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                string vsPath = Path.Combine(dir.FullName, ".vs");
                if (Directory.Exists(vsPath))
                    return vsPath;
                dir = dir.Parent;
            }
            return null;
        }

        // ====================================================================
        // Reader helpers
        // ====================================================================

        private static void ReadSymbols(SqliteCommand cmd, List<DbSymbol> results)
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadRow(reader));
        }

        private static DbSymbol? ReadSingleSymbol(SqliteCommand cmd)
        {
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }

        private static DbSymbol ReadRow(SqliteDataReader r) => new DbSymbol
        {
            Name        = r.IsDBNull(0) ? string.Empty : r.GetString(0),
            Kind        = r.IsDBNull(1) ? 0            : r.GetInt32(1),
            ReturnType  = r.IsDBNull(2) ? null         : r.GetString(2),
            Sourcecode  = r.IsDBNull(3) ? null         : r.GetString(3),
            XmlComments = r.IsDBNull(4) ? null         : r.GetString(4),
            FileName    = r.IsDBNull(5) ? null         : r.GetString(5),
            // DB lines are 0-based (verified empirically).
            StartLine   = r.IsDBNull(6) ? 0            : r.GetInt32(6),
            StartCol    = r.IsDBNull(7) ? 0            : r.GetInt32(7),
            // Column 8 (TypeName) is only present in queries that JOIN with Types.
            TypeName    = r.FieldCount > 8 && !r.IsDBNull(8) ? r.GetString(8) : null,
        };

        // ====================================================================
        // Utilities
        // ====================================================================

        /// <summary>
        /// Escapes LIKE-special characters (<c>%</c>, <c>_</c>, <c>\</c>) in
        /// <paramref name="s"/> so it is safe to use as a LIKE prefix pattern.
        /// </summary>
        private static string EscapeLike(string s)
            => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

        /// <inheritdoc/>
        public void Dispose()
        {
            _debounce?.Dispose();
            _debounce = null;

            _watcher?.Dispose();
            _watcher = null;

            lock (_lock) { CloseConnection(); }
        }
    }
}
