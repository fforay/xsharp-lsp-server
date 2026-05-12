using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>workspace/symbol</c> LSP request — project-wide symbol search
    /// (the "Go to Symbol in Workspace" / Ctrl+T feature in VS Code).
    /// <para>
    /// Strategy:
    /// <list type="number">
    ///   <item>If the query string is empty, return an empty result — avoids
    ///         dumping the entire database on every keystroke before the user
    ///         has typed anything.</item>
    ///   <item>Call <see cref="XSharpDatabaseService.FindByPrefix"/> with the
    ///         query string to retrieve up to 50 matching types and global
    ///         members from the XSharp IntelliSense database.</item>
    ///   <item>Convert each <see cref="Models.DbSymbol"/> to a
    ///         <see cref="SymbolInformation"/> carrying the name, kind, and
    ///         the file + line where the symbol is declared.</item>
    /// </list>
    /// Returns <c>null</c> when the database is unavailable.
    /// </para>
    /// </summary>
    public class XSharpWorkspaceSymbolHandler : WorkspaceSymbolsHandlerBase
    {
        private readonly XSharpDatabaseService                   _dbService;
        private readonly ILogger<XSharpWorkspaceSymbolHandler>   _logger;

        private const int MaxResults = 50;

        public XSharpWorkspaceSymbolHandler(
            XSharpDatabaseService                   dbService,
            ILogger<XSharpWorkspaceSymbolHandler>   logger)
        {
            _dbService = dbService;
            _logger    = logger;
        }

        /// <inheritdoc/>
        protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(
            WorkspaceSymbolCapability   capability,
            ClientCapabilities          clientCapabilities)
            => new WorkspaceSymbolRegistrationOptions();

        /// <inheritdoc/>
        public override Task<Container<WorkspaceSymbol>?> Handle(
            WorkspaceSymbolParams request,
            CancellationToken     cancellationToken)
        {
            try
            {
                string query = request.Query?.Trim() ?? string.Empty;

                if (!_dbService.IsAvailable || query.Length == 0)
                    return Task.FromResult<Container<WorkspaceSymbol>?>(null);

                _logger.LogInformation("WorkspaceSymbol: query='{Query}'", query);

                var dbSymbols = _dbService.FindByPrefix(query, MaxResults);
                if (dbSymbols.Count == 0)
                    return Task.FromResult<Container<WorkspaceSymbol>?>(null);

                var results = new List<WorkspaceSymbol>(dbSymbols.Count);
                var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var sym in dbSymbols)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(sym.Name)) continue;
                    if (!seen.Add(sym.Name))             continue;

                    var kind = DbKindToSymbolKind(sym.Kind);

                    if (string.IsNullOrEmpty(sym.FileName)) continue;

                    var uri      = DocumentUri.FromFileSystemPath(sym.FileName);
                    var location = new Location
                    {
                        Uri   = uri,
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(sym.StartLine, sym.StartCol),
                            new Position(sym.StartLine, sym.StartCol + sym.Name.Length)),
                    };

                    results.Add(new WorkspaceSymbol
                    {
                        Name          = sym.Name,
                        Kind          = kind,
                        Location      = new LocationOrFileLocation(location),
                        ContainerName = sym.ReturnType,
                    });
                }

                _logger.LogInformation(
                    "WorkspaceSymbol: {Count} result(s) for '{Query}'", results.Count, query);

                return Task.FromResult<Container<WorkspaceSymbol>?>(
                    results.Count > 0 ? new Container<WorkspaceSymbol>(results) : null);
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult<Container<WorkspaceSymbol>?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WorkspaceSymbol failed for query '{Query}'", request.Query);
                return Task.FromResult<Container<WorkspaceSymbol>?>(null);
            }
        }

        private static SymbolKind DbKindToSymbolKind(int kind) => kind switch
        {
            1  => SymbolKind.Class,       // Class
            2  => SymbolKind.Method,      // Method
            3  => SymbolKind.Property,    // Access/Assign
            4  => SymbolKind.Field,       // Field / iVar
            5  => SymbolKind.Function,    // Function
            6  => SymbolKind.Function,    // Procedure
            7  => SymbolKind.Variable,    // Global
            8  => SymbolKind.Interface,   // Interface
            9  => SymbolKind.Struct,      // Structure
            10 => SymbolKind.Enum,        // Enum
            11 => SymbolKind.EnumMember,  // Enum member
            _  => SymbolKind.Object,
        };
    }
}
