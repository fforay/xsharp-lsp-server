using LanguageService.CodeAnalysis;
using LanguageService.CodeAnalysis.Text;
using LanguageService.CodeAnalysis.XSharp;
using LanguageService.CodeAnalysis.XSharp.SyntaxParser;
using LanguageService.SyntaxTree;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models; // Assurez-vous que cette ligne est là !
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XSharp.Parser;


namespace XSharpLanguageServer
{
    public class XSharpSemanticTokensHandler : SemanticTokensHandlerBase, VsParser.IErrorListener
    {
        private readonly ILogger<XSharpSemanticTokensHandler> _logger;

        public XSharpSemanticTokensHandler(ILogger<XSharpSemanticTokensHandler> logger)
        {
            _logger = logger;
        }



        protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(SemanticTokensCapability capability, ClientCapabilities clientCapabilities)
        {
            // Les types et modificateurs de tokens définis par la capacité du client sont utilisés pour la légende.
            var legend = new SemanticTokensLegend
            {
                TokenTypes = capability.TokenTypes,
                TokenModifiers = capability.TokenModifiers
            };

            // Si vous voulez définir vos propres types X# spécifiques, faites-le ici :
            // legend.TokenTypes = new[] { SemanticTokenType.Keyword, SemanticTokenType.Class, "xsharpFunction" };

            // Le retour de cette méthode est mis à disposition via la propriété 'RegistrationOptions' de la classe de base.
            return new SemanticTokensRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("xsharp"),
                Legend = legend,
                Full = true,
                Range = true                
            };
        }

        protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(ITextDocumentIdentifierParams @params, CancellationToken cancellationToken)
        {
            // On utilise RegistrationOptions.Legend pour obtenir la légende définie ci-dessus.
            return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
        }

        protected override async Task Tokenize(SemanticTokensBuilder builder, ITextDocumentIdentifierParams identifier, CancellationToken cancellationToken)
        {
            try
            {
                var filePath = DocumentUri.GetFileSystemPath(identifier.TextDocument.Uri);

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return;

                string code = await File.ReadAllTextAsync(filePath, cancellationToken);

                string fileName = Path.GetFileName(filePath);
                XSharpParseOptions parseOptions;
                parseOptions = XSharpParseOptions.Default;

                bool ok = XSharp.Parser.VsParser.Lex(code, fileName, parseOptions, this, out ITokenStream tokenStream, out var _);
                var stream = tokenStream as BufferedTokenStream;
                var tokens = new List<IToken>();
                tokens.AddRange(stream.GetTokens());
                foreach (XSharpToken token in tokens)
                {
                    String tokenType = String.Empty;
                    //
                    if (XSharpLexer.IsKeyword(token.Type))
                    {
                        tokenType = SemanticTokenType.Keyword;
                    }
                    else if (XSharpLexer.IsIdentifier(token.Type))
                    {
                        tokenType = SemanticTokenType.Variable;
                    }
                    else if (XSharpLexer.IsComment(token.Type))
                    {
                        tokenType = SemanticTokenType.Comment;
                    }
                    else if (XSharpLexer.IsModifier(token.Type))
                    {
                        tokenType = SemanticTokenType.Modifier;
                    }
                    else if (XSharpLexer.IsString(token.Type))
                    {
                        tokenType = SemanticTokenType.String;
                    }
                    //
                    if (!String.IsNullOrEmpty(tokenType))
                    {
                        builder.Push(
                            token.Line - 1,
                            token.Column,
                            token.Text.Length,
                            tokenType,
                            new string[] { });
                    }


                }

                // --- ZONE DE TEST (MOCK) ---
                //builder.Push(0, 0, 5, SemanticTokenType.Keyword, new string[] { });
                // --- FIN ZONE DE TEST ---
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la tokenization");
            }
        }

        #region IErrorListener Members
        public void ReportError(string fileName, LinePositionSpan span, string errorCode, string message, object[] args)
        {
            //throw new NotImplementedException();
        }

        public void ReportWarning(string fileName, LinePositionSpan span, string errorCode, string message, object[] args)
        {
            //throw new NotImplementedException();
        }
        #endregion

    }
}