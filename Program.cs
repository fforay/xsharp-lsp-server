using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace XSharpLanguageServer
{
    /// <summary>
    /// Entry point for the XSharp LSP server process.
    /// <para>
    /// The server communicates with the LSP client (e.g. VS Code) over
    /// <c>stdin</c>/<c>stdout</c> using JSON-RPC, as required by the
    /// Language Server Protocol specification.
    /// </para>
    /// <para>
    /// Startup sequence:
    /// <list type="number">
    ///   <item>Configure Serilog logging (file or debug output).</item>
    ///   <item>Build the OmniSharp <see cref="LanguageServer"/>, registering
    ///         services and handlers via the DI container.</item>
    ///   <item>Wire up the <see cref="XSharpDiagnosticsPublisher"/> into
    ///         <see cref="XSharpDocumentService"/> — this must happen after the
    ///         server is built because <c>ILanguageServerFacade</c> is only
    ///         available at that point.</item>
    ///   <item>Wait for the client to send a <c>shutdown</c> + <c>exit</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            // ----------------------------------------------------------------
            // Logging
            // ----------------------------------------------------------------
            // If XSHARPLSP_LOG_PATH is set, write a rolling daily log file there.
            // Otherwise fall back to Debug output only (visible in a debugger).
            var logPath = Environment.GetEnvironmentVariable("XSHARPLSP_LOG_PATH");
            if (!string.IsNullOrEmpty(logPath))
            {
                var logFile = Path.Combine(logPath, "XSharpLSPServer.log");

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .WriteTo.File(logFile, rollingInterval: RollingInterval.Day)
                    .WriteTo.Debug()
                    .CreateLogger();
                Log.Information("Starting XSharp Language Server, log file: {LogFile}", logFile);
            }
            else
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .WriteTo.Debug()
                    .CreateLogger();
                Log.Information("Starting XSharp Language Server");
            }

            // ----------------------------------------------------------------
            // Server setup
            // ----------------------------------------------------------------
            var server = await LanguageServer.From(options =>
                options
                    // Standard LSP transport: stdin for client→server messages,
                    // stdout for server→client messages.
                    .WithInput(Console.OpenStandardInput())
                    .WithOutput(Console.OpenStandardOutput())
                    .WithServices(services =>
                    {
                        // Route Microsoft.Extensions.Logging through Serilog.
                        services.AddLogging(builder =>
                        {
                            builder.ClearProviders();
                            builder.AddSerilog(Log.Logger, dispose: true);
                        });

                        // Central document service: owns the text buffer and parse cache.
                        // Registered as a singleton so all handlers share one instance.
                        services.AddSingleton<XSharpDocumentService>();

                        // Database service: read-only access to X#Model.xsdb.
                        // TryConnect() is called via IOnLanguageServerInitialized once
                        // the workspace root is known from the LSP initialize request.
                        services.AddSingleton<XSharpDatabaseService>();

                        // Diagnostics publisher: sends textDocument/publishDiagnostics
                        // notifications after each parse. Registered as a singleton and
                        // wired into XSharpDocumentService below (after server build),
                        // because ILanguageServerFacade is not available inside WithServices.
                        services.AddSingleton<XSharpDiagnosticsPublisher>();
                    })
                    // Document sync: didOpen / didChange / didSave / didClose
                    .WithHandler<XSharpTextDocumentSyncHandler>()
                    // Semantic tokens: textDocument/semanticTokens/full and /range
                    .WithHandler<XSharpSemanticTokensHandler>()
                    // Document symbols: textDocument/documentSymbol (outline, breadcrumbs)
                    .WithHandler<XSharpDocumentSymbolHandler>()
                    // Folding ranges: textDocument/foldingRange (collapse blocks, #region, comments)
                    .WithHandler<XSharpFoldingRangeHandler>()
                    // Completion: textDocument/completion (keywords + document symbols)
                    .WithHandler<XSharpCompletionHandler>()
                    // Hover: textDocument/hover (prototype + doc comments from DB)
                    .WithHandler<XSharpHoverHandler>()
                    // Go-to-definition: textDocument/definition (file + line from DB)
                    .WithHandler<XSharpGoToDefinitionHandler>()
                    // Signature help: textDocument/signatureHelp (overloads from DB)
                    .WithHandler<XSharpSignatureHelpHandler>()
                    // Connect the DB service once the workspace root is known from the LSP
                    // initialize request (rootUri preferred, rootPath as fallback).
                    .OnInitialized((server, request, result, ct) =>
                    {
                        var db = server.Services.GetRequiredService<XSharpDatabaseService>();
                        string? root = request.RootUri?.GetFileSystemPath()
                                    ?? request.RootPath;
                        if (!string.IsNullOrEmpty(root))
                            db.TryConnect(root);
                        return Task.CompletedTask;
                    })
            );

            // ----------------------------------------------------------------
            // Post-build wiring
            // ----------------------------------------------------------------
            // ILanguageServerFacade is now available. Inject the diagnostics
            // publisher into the document service so it can push diagnostics
            // to the client after each parse.
            var docService    = server.Services.GetRequiredService<XSharpDocumentService>();
            var diagPublisher = server.Services.GetRequiredService<XSharpDiagnosticsPublisher>();
            docService.SetDiagnosticsPublisher(diagPublisher);

            // Block until the client sends shutdown + exit.
            await server.WaitForExit;
        }
    }
}
