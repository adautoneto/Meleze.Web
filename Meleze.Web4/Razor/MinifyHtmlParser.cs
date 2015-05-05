using System.Linq;
using System.Text;
using System.Web.Razor.Generator;
using System.Web.Razor.Parser;
using System.Web.Razor.Parser.SyntaxTree;
using System.Web.Razor.Text;
using System.Web.Razor.Tokenizer.Symbols;

namespace Meleze.Web.Razor
{
    /// <summary>
    /// MinifyHtmlParser delegates the document parsing to the original parser and then rewrite the parsed tokens
    /// with the HTML minifier.
    /// </summary>
    internal sealed class MinifyHtmlParser : ParserBase
    {
        private ParserBase _other;
        private MinifyHtmlMinifier _minifier;

        public MinifyHtmlParser(ParserBase other, MinifyHtmlMinifier minifier)
        {
            _other = other;
            _minifier = minifier;
        }

        public override void ParseDocument()
        {
            // The view is parsed by the default parser.
            // Then the tokens are rearranged to take into account HTML attributes (which as specific tokens).
            // Finally, the tokens are rewritten by the minifier and the resulting block is sent to the CodeGenerator.

            // Get the non-minified tokens
            _other.Context = Context;
            _other.ParseDocument();
            var block = Context.CurrentBlock;

            MinifyMarkup(block);
        }

        private void MinifyMarkup(BlockBuilder block)
        {
            var codeGenerator = new MarkupCodeGenerator();
            var previousIsWhiteSpace = true;
            var previousTokenEndsWithBlockElement = true;
            var insideScript = false;

            for (int i = 0; i < block.Children.Count; i++)
            {
                var node = block.Children[i];
                var span = node as Span;
                if (span == null)
                {
                    // When we have a dynamic markup, we can't know if the last char will be whitespace
                    // => to make it work in all cases, we won't minifiy whitespace just after code.
                    previousIsWhiteSpace = false;
                    previousTokenEndsWithBlockElement = false;

                    var section = node as Block;
                    if ((section != null) && (section.Type == BlockType.Section || section.Type == BlockType.Statement || section.Type == BlockType.Expression))
                    {
                        // Sections are special as they force us to recurse the minification
                        block.Children[i] = MinifySectionBlock(section);
                        previousIsWhiteSpace = false;
                        previousTokenEndsWithBlockElement = false;
                    }
                    continue;
                }

                var content = span.Content;
                if (string.IsNullOrEmpty(content))
                {
                    // Nothing to minify
                    block.Children.RemoveAt(i);
                    continue;
                }

                content = _minifier.Minify(content, previousIsWhiteSpace, previousTokenEndsWithBlockElement, insideScript);

                _minifier.AnalyseContent(content, ref previousIsWhiteSpace, ref previousTokenEndsWithBlockElement, ref insideScript);

                // We replace the content with the minified markup
                // and then let the CSharp/VB generator do their jobs.
                var builder = new SpanBuilder() { CodeGenerator = codeGenerator, EditHandler = span.EditHandler, Kind = span.Kind, Start = span.Start };
                var symbol = new MarkupSymbol() { Content = content };
                builder.Accept(symbol);
                span.ReplaceWith(builder);
            }
        }

        private Block MinifySectionBlock(Block block)
        {
            var builder = new BlockBuilder(block);

            // In sections, we only change the Markup blocks
            // as the others handle the section integration in the calling page.
            for (int i = 0; i < builder.Children.Count; i++)
            {
                var node = builder.Children[i];

                Span span = node as Span;
                if (!node.IsBlock && span != null && span.Kind == SpanKind.Code)
                {
                    node = MinifyCodeSpan(span);
                    builder.Children[i] = node;
                    continue;
                }

                var markup = node as Block;
                if ((markup == null) || (markup.Type != BlockType.Markup))
                {
                    continue;
                }

                var markupbuilder = new BlockBuilder(markup);
                MinifyMarkup(markupbuilder);
                markup = new Block(markupbuilder);
                builder.Children[i] = markup;
            }

            block = new Block(builder);
            return block;
        }

        private Span MinifyCodeSpan(Span span)
        {
            var clearableSymbols = span.Symbols
                .OfType<CSharpSymbol>()
                .Where(symbol =>
                {
                    var isEmptyWhiteSpace = symbol.Type == CSharpSymbolType.WhiteSpace && symbol.Content.Length > 1;
                    return symbol.Type == CSharpSymbolType.NewLine || isEmptyWhiteSpace;
                });

            var filteredSymbols = span.Symbols.Except<ISymbol>(clearableSymbols);
            var builder = new SpanBuilder(span);
            builder.ClearSymbols();
            foreach (var symbol in filteredSymbols)            
                builder.Accept(symbol);

            return new Span(builder);
        }

        private sealed class MarkupSymbol : ISymbol
        {
            private string _content;
            private SourceLocation _start = SourceLocation.Zero;

            public void ChangeStart(SourceLocation newStart)
            {
                _start = newStart;
            }

            public string Content
            {
                get { return _content; }
                set { _content = value; }
            }

            public void OffsetStart(SourceLocation documentStart)
            {
                _start = documentStart;
            }

            public SourceLocation Start
            {
                get { return _start; }
            }
        }

        #region ParserBase implementation

        public override void BuildSpan(System.Web.Razor.Parser.SyntaxTree.SpanBuilder span, System.Web.Razor.Text.SourceLocation start, string content)
        {
            _other.BuildSpan(span, start, content);
        }

        protected override ParserBase OtherParser
        {
            get { return Context.CodeParser; }
        }

        public override void ParseBlock()
        {
            _other.ParseBlock();
        }

        public override void ParseSection(System.Tuple<string, string> nestingSequences, bool caseSensitive)
        {
            _other.ParseSection(nestingSequences, caseSensitive);
        }

        #endregion
    }
}
