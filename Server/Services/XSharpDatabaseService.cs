using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        // ====================================================================
        // Assembly-only queries  (Tier-2 fallback for step-6 two-tier lookup)
        // ====================================================================

        /// <summary>
        /// Returns assembly-level types and globals whose name starts with
        /// <paramref name="prefix"/>. Queries <c>ReferencedTypes</c> and
        /// <c>ReferencedGlobals</c> only — no project source tables.
        /// </summary>
        public IReadOnlyList<WorkspaceSymbol> FindAssemblyByPrefix(string prefix, int maxResults = 100)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(prefix))
                return Array.Empty<WorkspaceSymbol>();

            var results = new List<WorkspaceSymbol>();
            try
            {
                const string typeSql = @"
                    SELECT rt.Name, rt.Kind, NULL AS ReturnType,
                           NULL AS Sourcecode, NULL AS XmlComments,
                           a.AssemblyFileName, 0, 0, NULL AS TypeName
                    FROM   ReferencedTypes rt
                    JOIN   Assemblies a ON a.Id = rt.idAssembly
                    WHERE  rt.Name LIKE @prefix ESCAPE '\'
                    LIMIT  @max";

                using var typeCmd = new SqliteCommand(typeSql, conn);
                typeCmd.Parameters.AddWithValue("@prefix", EscapeLike(prefix) + "%");
                typeCmd.Parameters.AddWithValue("@max", maxResults);
                ReadWorkspaceSymbols(typeCmd, results);

                const string globalSql = @"
                    SELECT rg.Name, rg.Kind, rg.ReturnType,
                           rg.Sourcecode, NULL AS XmlComments,
                           a.AssemblyFileName, 0, 0, NULL AS TypeName
                    FROM   ReferencedGlobals rg
                    JOIN   Assemblies a ON a.Id = rg.idAssembly
                    WHERE  rg.Name LIKE @prefix ESCAPE '\'
                    LIMIT  @max";

                using var globalCmd = new SqliteCommand(globalSql, conn);
                globalCmd.Parameters.AddWithValue("@prefix", EscapeLike(prefix) + "%");
                globalCmd.Parameters.AddWithValue("@max", maxResults);
                ReadWorkspaceSymbols(globalCmd, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindAssemblyByPrefix failed for '{Prefix}'", prefix);
            }

            return results;
        }

        /// <summary>
        /// Returns the namespace of an assembly-level type by exact name, or
        /// <c>null</c> when the type is not found or has no namespace recorded.
        /// Used by the "Add USING" code action.
        /// </summary>
        public string? FindAssemblyTypeNamespace(string name)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(name)) return null;

            try
            {
                const string sql = @"
                    SELECT rt.Namespace
                    FROM   ReferencedTypes rt
                    WHERE  rt.Name = @name COLLATE NOCASE
                      AND  rt.Namespace IS NOT NULL
                      AND  rt.Namespace != ''
                    LIMIT  1";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@name", name);
                var result = cmd.ExecuteScalar();
                return result as string;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindAssemblyTypeNamespace failed for '{Name}'", name);
                return null;
            }
        }

        /// <summary>
        /// Looks up an assembly-level symbol by exact name.
        /// Searches <c>ReferencedTypes</c> then <c>ReferencedGlobals</c>.
        /// Returns <c>null</c> if not found or DB unavailable.
        /// </summary>
        public WorkspaceSymbol? FindAssemblyExact(string name)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(name))
                return null;

            try
            {
                const string typeSql = @"
                    SELECT rt.Name, rt.Kind, NULL AS ReturnType,
                           NULL AS Sourcecode, NULL AS XmlComments,
                           a.AssemblyFileName, 0, 0, NULL AS TypeName
                    FROM   ReferencedTypes rt
                    JOIN   Assemblies a ON a.Id = rt.idAssembly
                    WHERE  rt.Name = @name COLLATE NOCASE
                    LIMIT  1";

                using var typeCmd = new SqliteCommand(typeSql, conn);
                typeCmd.Parameters.AddWithValue("@name", name);
                var typeResult = ReadSingleWorkspaceSymbol(typeCmd);
                if (typeResult != null) return typeResult;

                const string globalSql = @"
                    SELECT rg.Name, rg.Kind, rg.ReturnType,
                           rg.Sourcecode, NULL AS XmlComments,
                           a.AssemblyFileName, 0, 0, NULL AS TypeName
                    FROM   ReferencedGlobals rg
                    JOIN   Assemblies a ON a.Id = rg.idAssembly
                    WHERE  rg.Name = @name COLLATE NOCASE
                    LIMIT  1";

                using var globalCmd = new SqliteCommand(globalSql, conn);
                globalCmd.Parameters.AddWithValue("@name", name);
                return ReadSingleWorkspaceSymbol(globalCmd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindAssemblyExact failed for '{Name}'", name);
                return null;
            }
        }

        /// <summary>
        /// Returns all overloads of a global function from referenced assemblies.
        /// Queries <c>ReferencedGlobals</c> only.
        /// </summary>
        public IReadOnlyList<WorkspaceSymbol> FindAssemblyOverloads(string name)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null || string.IsNullOrEmpty(name))
                return Array.Empty<WorkspaceSymbol>();

            var results = new List<WorkspaceSymbol>();
            try
            {
                const string sql = @"
                    SELECT rg.Name, rg.Kind, rg.ReturnType,
                           rg.Sourcecode, NULL AS XmlComments,
                           a.AssemblyFileName, 0, 0, NULL AS TypeName
                    FROM   ReferencedGlobals rg
                    JOIN   Assemblies a ON a.Id = rg.idAssembly
                    WHERE  rg.Name = @name COLLATE NOCASE";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@name", name);
                ReadWorkspaceSymbols(cmd, results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FindAssemblyOverloads failed for '{Name}'", name);
            }

            return results;
        }

        // ====================================================================
        // Assembly member reflection  (for member completion after . / :)
        // ====================================================================

        /// <summary>
        /// Maps XSharp built-in type keywords to their .NET FullName equivalents
        /// so that reflection-based member lookup works for language primitives.
        /// </summary>
        private static readonly Dictionary<string, string> _xsharpTypeMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            { "STRING",   "System.String"  },
            { "INT",      "System.Int32"   },
            { "INT32",    "System.Int32"   },
            { "LONG",     "System.Int32"   },
            { "LONGINT",  "System.Int32"   },
            { "DWORD",    "System.UInt32"  },
            { "UINT32",   "System.UInt32"  },
            { "WORD",     "System.UInt16"  },
            { "UINT16",   "System.UInt16"  },
            { "BYTE",     "System.Byte"    },
            { "SHORTINT", "System.Int16"   },
            { "INT16",    "System.Int16"   },
            { "INT64",    "System.Int64"   },
            { "UINT64",   "System.UInt64"  },
            { "REAL4",    "System.Single"  },
            { "SINGLE",   "System.Single"  },
            { "REAL8",    "System.Double"  },
            { "DOUBLE",   "System.Double"  },
            { "FLOAT",    "System.Double"  },
            { "LOGIC",    "System.Boolean" },
            { "BOOL",     "System.Boolean" },
            { "BOOLEAN",  "System.Boolean" },
            { "CHAR",     "System.Char"    },
            { "OBJECT",   "System.Object"  },
            { "DYNAMIC",  "System.Object"  },
        };

        // Cache: XSharp type name (upper) → reflected members.
        // Never evicted — assembly metadata is stable for the lifetime of the server.
        private readonly ConcurrentDictionary<string, IReadOnlyList<WorkspaceSymbol>>
            _memberCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the public members (methods, properties, fields) of the .NET
        /// type corresponding to <paramref name="typeName"/>, via reflection.
        /// <para>
        /// Resolution order:
        /// <list type="number">
        ///   <item>XSharp built-in type alias map (e.g. <c>STRING</c> → <c>System.String</c>).</item>
        ///   <item><c>Type.GetType(typeName)</c> — covers fully-qualified BCL names.</item>
        ///   <item>Search through all assemblies already loaded in the AppDomain.</item>
        ///   <item>Load the assembly from the path recorded in <c>X#Model.xsdb</c> for
        ///         this type (covers NuGet and project-referenced assemblies).</item>
        /// </list>
        /// Results are cached indefinitely (assembly metadata does not change while
        /// the server is running).
        /// </para>
        /// </summary>
        public IReadOnlyList<WorkspaceSymbol> FindAssemblyMembersOf(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return Array.Empty<WorkspaceSymbol>();

            return _memberCache.GetOrAdd(typeName, name =>
            {
                var type = ResolveType(name);
                return type != null
                    ? ReflectMembers(type, name)
                    : Array.Empty<WorkspaceSymbol>();
            });
        }

        /// <summary>
        /// Attempts to resolve a .NET <see cref="Type"/> for <paramref name="name"/>.
        /// </summary>
        private Type? ResolveType(string name)
        {
            // 1. XSharp keyword alias → fully-qualified .NET name
            if (_xsharpTypeMap.TryGetValue(name, out var fullName))
            {
                var t = Type.GetType(fullName);
                if (t != null) return t;
            }

            // 2. Try as-is (may already be a FullName like "System.Collections.Generic.List`1")
            var direct = Type.GetType(name, throwOnError: false, ignoreCase: true);
            if (direct != null) return direct;

            // 3. Search already-loaded assemblies (covers most BCL + NuGet types)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, throwOnError: false, ignoreCase: true);
                    if (t != null) return t;
                }
                catch { /* some dynamic assemblies throw on GetType */ }
            }

            // 4. Look up the assembly file path from the DB and try loading it
            var asmPath = FindAssemblyFileForType(name);
            if (asmPath != null && File.Exists(asmPath))
            {
                try
                {
                    var asm = Assembly.LoadFrom(asmPath);
                    // Try both the short name and fully-qualified name
                    var t = asm.GetType(name, throwOnError: false, ignoreCase: true);
                    if (t != null) return t;

                    // Also search by short name (type may be in a namespace)
                    return asm.GetTypes().FirstOrDefault(
                        x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FindAssemblyMembersOf: could not load assembly {Path}", asmPath);
                }
            }

            return null;
        }

        /// <summary>
        /// Queries the DB for the <c>AssemblyFileName</c> of the assembly that
        /// contains <paramref name="typeName"/> (matched via <c>ReferencedTypes.Name</c>).
        /// </summary>
        private string? FindAssemblyFileForType(string typeName)
        {
            SqliteConnection? conn;
            lock (_lock) { conn = _connection; }
            if (conn == null) return null;

            try
            {
                const string sql = @"
                    SELECT a.AssemblyFileName
                    FROM   ReferencedTypes rt
                    JOIN   Assemblies a ON a.Id = rt.idAssembly
                    WHERE  rt.Name = @name COLLATE NOCASE
                    LIMIT  1";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@name", typeName);
                return cmd.ExecuteScalar() as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// Reflects public methods, properties, and fields from <paramref name="type"/>
        /// — including inherited members up the base-class chain — and returns them
        /// as <see cref="WorkspaceSymbol"/> instances.
        /// Special-name members (property accessors, event add/remove) and members
        /// declared on <see cref="object"/> (other than <c>ToString</c>) are excluded.
        /// </summary>
        private static IReadOnlyList<WorkspaceSymbol> ReflectMembers(Type type, string typeName)
        {
            var results = new List<WorkspaceSymbol>();
            // FlattenHierarchy is required for inherited *static* members — instance
            // members are already inherited without it. The Object-member filter
            // below excludes the handful of extra Object statics it surfaces
            // (Equals(object,object), ReferenceEquals).
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.FlattenHierarchy;

            // Assembly.Location returns empty string in single-file publish — use it
            // only as an informational label; navigation is not expected for BCL types.
#pragma warning disable IL3000
            string asmLocation = type.Assembly.Location;
#pragma warning restore IL3000

            // Methods
            foreach (var m in type.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;   // skip get_X, set_X, add_X, remove_X
                if (m.DeclaringType == typeof(object) && m.Name != "ToString") continue;

                var paramStr = string.Join(", ",
                    m.GetParameters().Select(p =>
                        $"{p.Name} AS {p.ParameterType.Name}"));

                results.Add(new WorkspaceSymbol
                {
                    Name       = m.Name,
                    Kind       = XSharpSymbolKind.Method,
                    ReturnType = m.ReturnType.Name,
                    Sourcecode = $"METHOD {m.Name}({paramStr}) AS {m.ReturnType.Name}",
                    FileName   = asmLocation,
                    TypeName   = typeName,
                });
            }

            // Properties
            foreach (var p in type.GetProperties(flags))
            {
                results.Add(new WorkspaceSymbol
                {
                    Name       = p.Name,
                    Kind       = XSharpSymbolKind.Property,
                    ReturnType = p.PropertyType.Name,
                    Sourcecode = $"PROPERTY {p.Name} AS {p.PropertyType.Name}",
                    FileName   = asmLocation,
                    TypeName   = typeName,
                });
            }

            // Fields (public only, non-special)
            foreach (var f in type.GetFields(flags))
            {
                if (f.IsSpecialName) continue;
                results.Add(new WorkspaceSymbol
                {
                    Name       = f.Name,
                    Kind       = XSharpSymbolKind.Field,
                    ReturnType = f.FieldType.Name,
                    Sourcecode = $"FIELD {f.Name} AS {f.FieldType.Name}",
                    FileName   = asmLocation,
                    TypeName   = typeName,
                });
            }

            return results;
        }

        // ====================================================================
        // Project-tier queries  (obsolete — replaced by XSharpWorkspaceIndex)
        // ====================================================================

        /// <summary>
        /// Returns all types and global members whose name starts with
        /// <paramref name="prefix"/> (case-insensitive).
        /// Used for cross-file completion.
        /// </summary>
        [Obsolete("Use two-tier lookup: XSharpWorkspaceIndex.FindByPrefix then FindAssemblyByPrefix")]
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
        [Obsolete("Use XSharpWorkspaceIndex.GetMembersOf — no assembly-level member table exists in the DB")]
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
        [Obsolete("Use two-tier lookup: XSharpWorkspaceIndex.FindExact then FindAssemblyExact")]
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
        [Obsolete("Use two-tier lookup: XSharpWorkspaceIndex.FindOverloads then FindAssemblyOverloads")]
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
        [Obsolete("Use XSharpWorkspaceIndex.FindAllByName — assembly symbols have no source locations")]
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

        private static void ReadWorkspaceSymbols(SqliteCommand cmd, List<WorkspaceSymbol> results)
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadWorkspaceRow(reader));
        }

        private static WorkspaceSymbol? ReadSingleWorkspaceSymbol(SqliteCommand cmd)
        {
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadWorkspaceRow(reader) : null;
        }

        private static WorkspaceSymbol ReadWorkspaceRow(SqliteDataReader r) => new WorkspaceSymbol
        {
            Name        = r.IsDBNull(0) ? string.Empty : r.GetString(0),
            Kind        = r.IsDBNull(1) ? 0            : r.GetInt32(1),
            ReturnType  = r.IsDBNull(2) ? null         : r.GetString(2),
            Sourcecode  = r.IsDBNull(3) ? null         : r.GetString(3),
            XmlComments = r.IsDBNull(4) ? null         : r.GetString(4),
            FileName    = r.IsDBNull(5) ? string.Empty : r.GetString(5),
            StartLine   = r.IsDBNull(6) ? 0            : r.GetInt32(6),
            StartCol    = r.IsDBNull(7) ? 0            : r.GetInt32(7),
            TypeName    = r.FieldCount > 8 && !r.IsDBNull(8) ? r.GetString(8) : null,
        };

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
