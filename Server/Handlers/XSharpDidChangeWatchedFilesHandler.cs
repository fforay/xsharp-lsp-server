using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XSharp.Parser;
using XSharpLanguageServer.Services;

namespace XSharpLanguageServer.Handlers
{
    /// <summary>
    /// Handles the <c>workspace/didChangeWatchedFiles</c> LSP notification.
    /// <para>
    /// Called by the client when XSharp source files (<c>*.prg</c>, <c>*.xs</c>,
    /// <c>*.xh</c>) are created, modified, or deleted outside the editor — for
    /// example by a build tool, a file copy, or a VCS checkout.
    /// </para>
    /// <para>
    /// Strategy:
    /// <list type="bullet">
    ///   <item>Created / Changed — re-parse the file and replace its entry in
    ///         <see cref="XSharpWorkspaceIndex"/>.</item>
    ///   <item>Deleted — remove its entry from <see cref="XSharpWorkspaceIndex"/>.</item>
    /// </list>
    /// Files under <c>bin\</c> or <c>obj\</c> directories are ignored because they
    /// are build artefacts and were excluded from the initial scan.
    /// </para>
    /// </summary>
    public class XSharpDidChangeWatchedFilesHandler : DidChangeWatchedFilesHandlerBase
    {
        private readonly ILogger<XSharpDidChangeWatchedFilesHandler> _logger;
        private readonly XSharpConfigurationService _configService;
        private readonly XSharpWorkspaceIndex _workspaceIndex;

        public XSharpDidChangeWatchedFilesHandler(
            ILogger<XSharpDidChangeWatchedFilesHandler> logger,
            XSharpConfigurationService configService,
            XSharpWorkspaceIndex workspaceIndex)
        {
            _logger         = logger;
            _configService  = configService;
            _workspaceIndex = workspaceIndex;
        }

        /// <inheritdoc/>
        public override Task<Unit> Handle(
            DidChangeWatchedFilesParams request,
            CancellationToken cancellationToken)
        {
            foreach (var fileEvent in request.Changes)
            {
                var path = fileEvent.Uri.GetFileSystemPath();
                if (path == null || IsOutputDirectory(path)) continue;

                switch (fileEvent.Type)
                {
                    case FileChangeType.Created:
                    case FileChangeType.Changed:
                        IndexFile(path);
                        break;

                    case FileChangeType.Deleted:
                        _workspaceIndex.RemoveFile(path);
                        _logger.LogDebug("WorkspaceIndex: removed {File} (deleted)", path);
                        break;
                }
            }

            return Unit.Task;
        }

        /// <inheritdoc/>
        protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
            DidChangeWatchedFilesCapability capability,
            ClientCapabilities clientCapabilities)
        {
            return new DidChangeWatchedFilesRegistrationOptions
            {
                Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(
                    new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                    {
                        GlobPattern = new GlobPattern("**/*.prg"),
                        Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                    },
                    new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                    {
                        GlobPattern = new GlobPattern("**/*.xs"),
                        Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                    },
                    new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                    {
                        GlobPattern = new GlobPattern("**/*.xh"),
                        Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                    }
                )
            };
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private void IndexFile(string filePath)
        {
            try
            {
                var text          = File.ReadAllText(filePath);
                var options       = _configService.GetParseOptions();
                var errorListener = new NullErrorListener();

                bool ok = VsParser.Parse(
                    text,
                    filePath,
                    options,
                    errorListener,
                    out _,
                    out var tree,
                    out _);

                if (!ok || tree == null) return;

                var symbols = IndexSymbolExtractor.Extract(tree, filePath, text);
                _workspaceIndex.UpdateFile(filePath, symbols);

                _logger.LogDebug("WorkspaceIndex: re-indexed {File} ({Count} symbol(s))",
                    Path.GetFileName(filePath), symbols.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WorkspaceIndex: failed to re-index {File}", filePath);
            }
        }

        private static bool IsOutputDirectory(string path)
        {
            var sep = Path.DirectorySeparatorChar;
            return path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
                || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class NullErrorListener : VsParser.IErrorListener
        {
            public void ReportError(string fileName, LanguageService.CodeAnalysis.Text.LinePositionSpan span,
                string errorCode, string message, object[] args) { }

            public void ReportWarning(string fileName, LanguageService.CodeAnalysis.Text.LinePositionSpan span,
                string errorCode, string message, object[] args) { }
        }
    }
}
