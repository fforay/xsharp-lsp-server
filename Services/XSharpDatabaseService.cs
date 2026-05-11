using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
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
        /// corresponds to the given <paramref name="workspaceRoot"/> directory.
        /// <para>
        /// The search walks upward from <paramref name="workspaceRoot"/> looking
        /// for a <c>.sln</c> file, then checks
        /// <c>.vs\&lt;solution-name&gt;\X#Model.xsdb</c>.
        /// </para>
        /// <para>
        /// Safe to call multiple times — subsequent calls are ignored if a
        /// connection is already open.
        /// </para>
        /// </summary>
        public void TryConnect(string workspaceRoot)
        {
            if (_connection != null) return;  // already connected

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

        /// <summary>Returns <c>true</c> if the database connection is open.</summary>
        public bool IsAvailable => _connection != null;

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
            if (_connection == null || string.IsNullOrEmpty(prefix))
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

                using var typeCmd = new SqliteCommand(typeSql, _connection);
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

                using var memberCmd = new SqliteCommand(memberSql, _connection);
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
            if (_connection == null || string.IsNullOrEmpty(typeName))
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

                using var cmd = new SqliteCommand(sql, _connection);
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
            if (_connection == null || string.IsNullOrEmpty(name))
                return null;

            try
            {
                // Try types first
                const string typeSql = @"
                    SELECT t.Name, t.Kind, NULL AS ReturnType,
                           t.Sourcecode, t.XmlComments,
                           f.FileName, t.StartLine, t.StartColumn
                    FROM   ProjectTypes t
                    JOIN   Files f ON f.Id = t.idFile
                    WHERE  t.Name = @name COLLATE NOCASE
                    ORDER  BY CASE WHEN f.FileName = @file THEN 0 ELSE 1 END
                    LIMIT  1";

                using var typeCmd = new SqliteCommand(typeSql, _connection);
                typeCmd.Parameters.AddWithValue("@name", name);
                typeCmd.Parameters.AddWithValue("@file", currentFile ?? string.Empty);
                var typeResult = ReadSingleSymbol(typeCmd);
                if (typeResult != null) return typeResult;

                // Then members
                const string memberSql = @"
                    SELECT m.Name, m.Kind, m.ReturnType,
                           m.Sourcecode, m.XmlComments,
                           f.FileName, m.StartLine, m.StartColumn
                    FROM   ProjectMembers m
                    JOIN   Files f ON f.Id = m.IdFile
                    WHERE  m.Name = @name COLLATE NOCASE
                    ORDER  BY CASE WHEN f.FileName = @file THEN 0 ELSE 1 END
                    LIMIT  1";

                using var memberCmd = new SqliteCommand(memberSql, _connection);
                memberCmd.Parameters.AddWithValue("@name", name);
                memberCmd.Parameters.AddWithValue("@file", currentFile ?? string.Empty);
                return ReadSingleSymbol(memberCmd);
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
            if (_connection == null || string.IsNullOrEmpty(name))
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

                using var cmd = new SqliteCommand(sql, _connection);
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
            if (_connection == null || string.IsNullOrEmpty(name))
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

                using var typeCmd = new SqliteCommand(typeSql, _connection);
                typeCmd.Parameters.AddWithValue("@name", name);
                ReadSymbols(typeCmd, results);

                const string memberSql = @"
                    SELECT m.Name, m.Kind, m.ReturnType,
                           m.Sourcecode, m.XmlComments,
                           f.FileName, m.StartLine, m.StartColumn
                    FROM   ProjectMembers m
                    JOIN   Files f ON f.Id = m.IdFile
                    WHERE  m.Name = @name COLLATE NOCASE";

                using var memberCmd = new SqliteCommand(memberSql, _connection);
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
            // DB lines are 1-based; LSP is 0-based.
            StartLine   = r.IsDBNull(6) ? 0            : Math.Max(0, r.GetInt32(6) - 1),
            StartCol    = r.IsDBNull(7) ? 0            : r.GetInt32(7),
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
            _connection?.Dispose();
            _connection = null;
        }
    }
}
