using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
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
    ///         <see cref="OmniSharp.Extensions.LanguageServer.Protocol.Models.DidChangeConfigurationParams.Settings"/>.</item>
    ///   <item>Passes the parsed settings to <see cref="XSharpConfigurationService.Apply(Newtonsoft.Json.Linq.JToken?)"/>.</item>
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

        public XSharpDidChangeConfigurationHandler(
            ILogger<XSharpDidChangeConfigurationHandler> logger,
            XSharpConfigurationService configService,
            XSharpDocumentService docService)
        {
            _logger        = logger;
            _configService = configService;
            _docService    = docService;
        }

        /// <inheritdoc/>
        public override Task<Unit> Handle(
            OmniSharp.Extensions.LanguageServer.Protocol.Models.DidChangeConfigurationParams request,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("workspace/didChangeConfiguration received — refreshing XSharp settings");

            // Extract settings from the notification payload.
            // OmniSharp exposes Settings as JToken? (Newtonsoft.Json).
            _configService.Apply(request.Settings);

            // Re-parse all open documents so diagnostics and features reflect
            // the new dialect / include paths / preprocessor symbols immediately.
            _docService.ReparseAll();

            return Task.FromResult(Unit.Value);
        }
    }
}
