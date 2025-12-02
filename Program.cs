using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection; // <-- indispensable pour AddSingleton
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;

namespace XSharpLanguageServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var server = await LanguageServer.From(options =>
                options.WithInput(Console.OpenStandardInput())
                       .WithOutput(Console.OpenStandardOutput())
                       // Enregistrer les services partagés
                       .WithServices(services =>
                       {
                           // Buffer partagé pour le contenu des documents
                           services.AddSingleton<IDictionary<DocumentUri, string>>(
                               sp => new Dictionary<DocumentUri, string>());

                           // Logger par défaut (optionnel, sinon tu peux ensuite injecter ILogger<>)
                           services.AddLogging();
                       })
                       // Enregistrer les handlers (résolus via DI, injection automatique du buffer)
                       .WithHandler<XSharpSemanticTokensHandler>()
                       .WithHandler<XSharpTextDocumentSyncHandler>()
            );

            await server.WaitForExit;
        }
    }
}