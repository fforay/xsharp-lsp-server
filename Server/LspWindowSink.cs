using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using Serilog.Core;
using Serilog.Events;

namespace XSharpLanguageServer
{
    /// <summary>
    /// Serilog sink that forwards log events to the LSP client's Output panel via
    /// <c>window/logMessage</c> notifications.
    /// <para>
    /// The sink is created before the OmniSharp server is built (so Serilog can be
    /// configured with it immediately) and activated later via <see cref="SetServer"/>
    /// once <see cref="ILanguageServerFacade"/> is available.  Log events emitted
    /// before activation are silently discarded.
    /// </para>
    /// </summary>
    internal sealed class LspWindowSink : ILogEventSink
    {
        private volatile ILanguageServerFacade? _server;

        /// <summary>Activates the sink; messages are forwarded from this point on.</summary>
        public void SetServer(ILanguageServerFacade server) => _server = server;

        /// <inheritdoc />
        public void Emit(LogEvent logEvent)
        {
            var srv = _server;
            if (srv == null) return;

            var type = logEvent.Level switch
            {
                LogEventLevel.Error or
                LogEventLevel.Fatal   => MessageType.Error,
                LogEventLevel.Warning => MessageType.Warning,
                LogEventLevel.Information => MessageType.Info,
                _                     => MessageType.Log
            };

            var prefix = logEvent.Level switch
            {
                LogEventLevel.Fatal       => "[FTL]",
                LogEventLevel.Error       => "[ERR]",
                LogEventLevel.Warning     => "[WRN]",
                LogEventLevel.Information => "[INF]",
                LogEventLevel.Debug       => "[DBG]",
                _                         => "[VRB]"
            };

            try
            {
                srv.Window.LogMessage(new LogMessageParams
                {
                    Type    = type,
                    Message = $"{prefix} {logEvent.RenderMessage()}"
                });
            }
            catch
            {
                // Never allow a logging failure to crash the server.
            }
        }
    }
}
