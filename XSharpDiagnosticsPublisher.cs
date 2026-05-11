using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Linq;

namespace XSharpLanguageServer
{
    /// <summary>
    /// Pushes XSharp parse diagnostics (errors and warnings) to the LSP client
    /// using the <c>textDocument/publishDiagnostics</c> notification.
    /// <para>
    /// This class is a singleton registered in the DI container and injected into
    /// <see cref="XSharpDocumentService"/> after server startup (see <c>Program.cs</c>).
    /// It is kept separate from the document service to isolate the dependency on
    /// <c>ILanguageServerFacade</c>, which is only available once the OmniSharp server
    /// has been fully initialised.
    /// </para>
    /// </summary>
    public sealed class XSharpDiagnosticsPublisher
    {
        private readonly ILanguageServerFacade _server;
        private readonly ILogger<XSharpDiagnosticsPublisher> _logger;

        /// <summary>
        /// Initialises the publisher. Called by the DI container.
        /// </summary>
        public XSharpDiagnosticsPublisher(
            ILanguageServerFacade server,
            ILogger<XSharpDiagnosticsPublisher> logger)
        {
            _server = server;
            _logger = logger;
        }

        /// <summary>
        /// Sends a <c>textDocument/publishDiagnostics</c> notification for
        /// <paramref name="uri"/> containing all diagnostics from <paramref name="parsed"/>.
        /// <para>
        /// Passing an empty diagnostics list clears any previously shown squiggles
        /// in the editor, which is the correct behaviour when a file becomes error-free.
        /// </para>
        /// </summary>
        /// <param name="uri">The document URI the diagnostics belong to.</param>
        /// <param name="parsed">The parse result whose diagnostics should be published.</param>
        public void Publish(DocumentUri uri, ParsedDocument parsed)
        {
            try
            {
                _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
                {
                    Uri = uri,
                    // Container<T> is OmniSharp's immutable wrapper around IEnumerable<T>.
                    Diagnostics = new Container<Diagnostic>(parsed.Diagnostics)
                });

                _logger.LogInformation(
                    "Published {Count} diagnostic(s) for {Uri}",
                    parsed.Diagnostics.Count,
                    uri);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to publish diagnostics for {Uri}", uri);
            }
        }
    }
}
