using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Server;
using XSharpLanguageServer;

class Program
{
    static async Task Main(string[] args)
    {
        var server = await LanguageServer.From(options =>
            options
                .WithInput(Console.OpenStandardInput())
                .WithOutput(Console.OpenStandardOutput())
                .WithLoggerFactory(new LoggerFactory())
                .AddDefaultLoggingProvider()
                .WithServices(services =>
                {
                    // Enregistrer vos services ici si besoin
                })
                .WithHandler<XSharpSemanticTokensHandler>()
        );

        await server.WaitForExit;
    }
}