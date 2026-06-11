using MediatR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>workspace/didChangeConfiguration</c> LSP notification.
    /// <para>
    /// When the client reports that workspace settings have changed this handler:
    /// <list type="number">
    ///   <item>Extracts the <c>xsharp</c> section from
    ///         <see cref="DidChangeConfigurationParams.Settings"/>.</item>
    ///   <item>When <c>Settings</c> is <c>null</c> (LSP 3.17+ / vscode-languageclient ≥ 7
    ///         sends null and expects the server to pull), issues a
    ///         <c>workspace/configuration</c> request to fetch the <c>xsharp</c> section.</item>
    ///   <item>Passes the resolved settings to <see cref="XSharpConfigurationService.Apply(Newtonsoft.Json.Linq.JToken?)"/>.</item>
    ///   <item>Triggers <see cref="XSharpDocumentService.ReparseAll"/> so all open
    ///         documents are re-parsed with the new dialect / include paths / defines.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class XSharpDidChangeConfigurationHandler : DidChangeConfigurationHandlerBase
    {
        private readonly ILogger<XSharpDidChangeConfigurationHandler> _logger;
        private readonly XSharpConfigurationService _configService;
        private readonly XSharpDocumentService _docService;
        private readonly ILanguageServerFacade _server;

        public XSharpDidChangeConfigurationHandler(
            ILogger<XSharpDidChangeConfigurationHandler> logger,
            XSharpConfigurationService configService,
            XSharpDocumentService docService,
            ILanguageServerFacade server)
        {
            _logger        = logger;
            _configService = configService;
            _docService    = docService;
            _server        = server;
        }

        /// <inheritdoc/>
        public override async Task<Unit> Handle(
            DidChangeConfigurationParams request,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("workspace/didChangeConfiguration received — refreshing XSharp settings");

            JToken? settings = request.Settings;

            // LSP 3.17+ clients (vscode-languageclient ≥ 7) send null settings;
            // pull the xsharp section via workspace/configuration instead.
            if (settings == null || settings.Type == JTokenType.Null)
            {
                _logger.LogDebug("didChangeConfiguration: settings is null — pulling via workspace/configuration");
                var result = await _server.Workspace.RequestConfiguration(
                    new ConfigurationParams
                    {
                        Items = new Container<ConfigurationItem>(
                            new ConfigurationItem { Section = "xsharp" })
                    },
                    cancellationToken);
                settings = result?.FirstOrDefault();
            }

            _configService.Apply(settings);

            // Re-parse all open documents so diagnostics and features reflect
            // the new dialect / include paths / preprocessor symbols immediately.
            _docService.ReparseAll();

            return Unit.Value;
        }
    }
}
