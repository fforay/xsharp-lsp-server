using LanguageService.CodeAnalysis.XSharp;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XSharpLanguageServer.Models;

namespace XSharpLanguageServer.Services
{
    /// <summary>
    /// Singleton service that owns the cached <see cref="XSharpWorkspaceSettings"/>
    /// and converts them into a ready-to-use <see cref="XSharpParseOptions"/> instance.
    /// <para>
    /// Settings are applied when the client sends a
    /// <c>workspace/didChangeConfiguration</c> notification (see
    /// <see cref="Apply(Newtonsoft.Json.Linq.JToken?)"/>).  Until then, Core-dialect
    /// defaults are used.
    /// </para>
    /// All public members are thread-safe.
    /// </summary>
    public sealed class XSharpConfigurationService
    {
        private readonly ILogger<XSharpConfigurationService> _logger;

        // Guards _settings and _parseOptions.
        private readonly object _lock = new();

        private XSharpWorkspaceSettings _settings = new();
        private XSharpParseOptions _parseOptions = BuildOptions(new());

        /// <summary>Initialises the service. Called by the DI container.</summary>
        public XSharpConfigurationService(ILogger<XSharpConfigurationService> logger)
        {
            _logger = logger;
        }

        // ----------------------------------------------------------------
        // Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the <see cref="XSharpParseOptions"/> built from the last
        /// successfully applied settings.  Falls back to Core-dialect defaults
        /// if no settings have been applied yet.
        /// </summary>
        public XSharpParseOptions GetParseOptions()
        {
            lock (_lock) { return _parseOptions; }
        }

        /// <summary>
        /// Returns a snapshot of the current workspace settings.
        /// </summary>
        public XSharpWorkspaceSettings GetSettings()
        {
            lock (_lock) { return _settings; }
        }

        /// <summary>
        /// Parses and applies settings from the JSON token sent by the client in a
        /// <c>workspace/didChangeConfiguration</c> notification.
        /// <para>
        /// Expected JSON shape (all fields optional):
        /// <code>
        /// { "xsharp": { "dialect": "VO", "includePaths": "C:\\Inc", "preprocessorSymbols": "DEBUG" } }
        /// </code>
        /// If <paramref name="settingsToken"/> is <c>null</c> or missing the
        /// <c>xsharp</c> key, the current settings are preserved.
        /// </para>
        /// </summary>
        public void Apply(JToken? settingsToken)
        {
            if (settingsToken == null)
            {
                _logger.LogDebug("didChangeConfiguration: settings token is null — keeping current settings");
                return;
            }

            try
            {
                // The settings object may be the full workspace settings object
                // (containing an "xsharp" key) or the xsharp section directly.
                JToken? xsharp = settingsToken["xsharp"] ?? settingsToken;

                var settings = new XSharpWorkspaceSettings
                {
                    Dialect             = xsharp["dialect"]?.Value<string>()             ?? GetSettings().Dialect,
                    IncludePaths        = xsharp["includePaths"]?.Value<string>()        ?? GetSettings().IncludePaths,
                    PreprocessorSymbols = xsharp["preprocessorSymbols"]?.Value<string>() ?? GetSettings().PreprocessorSymbols,
                };

                Apply(settings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse workspace/didChangeConfiguration settings");
            }
        }

        /// <summary>
        /// Applies a pre-built <see cref="XSharpWorkspaceSettings"/> instance.
        /// </summary>
        public void Apply(XSharpWorkspaceSettings settings)
        {
            var options = BuildOptions(settings);
            lock (_lock)
            {
                _settings     = settings;
                _parseOptions = options;
            }
            _logger.LogInformation(
                "XSharp parse options updated: Dialect={Dialect}, IncludePaths={Paths}, Symbols={Symbols}",
                settings.Dialect, settings.IncludePaths, settings.PreprocessorSymbols);
        }

        // ----------------------------------------------------------------
        // Private helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Converts an <see cref="XSharpWorkspaceSettings"/> into an
        /// <see cref="XSharpParseOptions"/> using <c>XSharpParseOptions.FromVsValues</c>,
        /// which accepts VS-style command-line option strings such as
        /// <c>/dialect:VO</c> and <c>/i:path1;path2</c>.
        /// </summary>
        private static XSharpParseOptions BuildOptions(XSharpWorkspaceSettings s)
        {
            var args = new List<string>();

            // Dialect — default to Core if blank or unrecognised.
            args.Add($"/dialect:{NormaliseDialect(s.Dialect)}");

            // Include paths — semicolon-separated list passed as /i:.
            if (!string.IsNullOrWhiteSpace(s.IncludePaths))
                args.Add($"/i:{s.IncludePaths.Trim()}");

            // Preprocessor symbols — each one becomes a /d: argument.
            if (!string.IsNullOrWhiteSpace(s.PreprocessorSymbols))
            {
                foreach (var sym in s.PreprocessorSymbols
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    args.Add($"/d:{sym.Trim()}");
                }
            }

            return XSharpParseOptions.FromVsValues(args);
        }

        /// <summary>
        /// Returns a canonical dialect name accepted by <c>/dialect:</c>, falling back
        /// to <c>"Core"</c> for unknown values.
        /// </summary>
        private static string NormaliseDialect(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Core";
            if (Enum.TryParse<XSharpDialect>(value, ignoreCase: true, out var d) &&
                d != XSharpDialect.Last)
                return d.ToString();
            return "Core";
        }
    }
}
