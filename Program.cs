using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using System;
using System.Threading.Tasks;

namespace XSharpLanguageServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
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

            var server = await LanguageServer.From(options =>
                options.WithInput(Console.OpenStandardInput())
                       .WithOutput(Console.OpenStandardOutput())
                       .WithServices(services =>
                       {
                           services.AddLogging(builder =>
                           {
                               builder.ClearProviders();
                               builder.AddSerilog(Log.Logger, dispose: true);
                           });

                           // Core document service — owns text buffer + parse cache
                           services.AddSingleton<XSharpDocumentService>();

                           // Diagnostics publisher — needs ILanguageServerFacade,
                           // which is only available after the server is built.
                           services.AddSingleton<XSharpDiagnosticsPublisher>();
                       })
                       .WithHandler<XSharpTextDocumentSyncHandler>()
                       .WithHandler<XSharpSemanticTokensHandler>()
            );

            // Wire the diagnostics publisher into the document service now that
            // ILanguageServerFacade is available.
            var docService   = server.Services.GetRequiredService<XSharpDocumentService>();
            var diagPublisher = server.Services.GetRequiredService<XSharpDiagnosticsPublisher>();
            docService.SetDiagnosticsPublisher(diagPublisher);

            await server.WaitForExit;
        }
    }
}
