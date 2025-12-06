using Microsoft.Extensions.DependencyInjection; // <-- indispensable pour AddSingleton
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using Serilog.Debugging;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace XSharpLanguageServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            //var exeInfo = new FileInfo(Assembly.GetEntryAssembly().Location);
            //var exePath = Path.GetDirectoryName(exeInfo.FullName);

            //var logPath = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
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
                Log.Information("Starting XSharp Language Server, log file : {LogFile}", logFile);

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
                       // Shared Services via Dependency Injection
                       .WithServices(services =>
                       {
                           // Shared buffer
                           services.AddSingleton<IDictionary<DocumentUri, string>>(
                               sp => new Dictionary<DocumentUri, string>());
                           services.AddLogging(builder =>
                           {
                               builder.ClearProviders();
                               builder.AddSerilog(Log.Logger, dispose: true);
                           });

                       })
                       // Register Handlers, resolved by DI
                       .WithHandler<XSharpSemanticTokensHandler>()
                       .WithHandler<XSharpTextDocumentSyncHandler>()
            );

            await server.WaitForExit;
        }
    }
}