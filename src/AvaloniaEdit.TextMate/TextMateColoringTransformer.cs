using System;
using System.Buffers;
using System.Collections.Generic;

using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextMateSharp.Grammars;
using TextMateSharp.Model;
using TextMateSharp.Themes;
using FontStyle = TextMateSharp.Themes.FontStyle;

namespace AvaloniaEdit.TextMate
{
    public class TextMateColoringTransformer :
        GenericLineTransformer,
        IModelTokensChangedListener
    {
        private Theme _theme;
        private IGrammar _grammar;
        private TMModel _model;
        private TextDocument _document;
        private TextView _textView;
        private Action<Exception> _exceptionHandler;

        private readonly Dictionary<int, IBrush> _brushes;

        public TextMateColoringTransformer(
            TextView textView,
            Action<Exception> exceptionHandler)
            : base(exceptionHandler)
        {
            _textView = textView;
            _exceptionHandler = exceptionHandler;

            _brushes = new Dictionary<int, IBrush>();
        }

        public void SetModel(TextDocument document, TMModel model)
        {
            _document = document;
            _model = model;

            if (_grammar != null)
            {
                _model.SetGrammar(_grammar);
            }
        }

        public void Dispose()
        {
            _brushes.Clear();
        }

        public void SetTheme(Theme theme)
        {
            _theme = theme;

            _brushes.Clear();

            var map = _theme.GetColorMap();

            foreach (var color in map)
            {
                var id = _theme.GetColorId(color);

                _brushes[id] = new ImmutableSolidColorBrush(Color.Parse(NormalizeColor(color)));
            }
        }

        public void SetGrammar(IGrammar grammar)
        {
            _grammar = grammar;

            if (_model != null)
            {
                _model.SetGrammar(grammar);
            }
        }

        protected override void TransformLine(DocumentLine line, ITextRunConstructionContext context)
        {
            try
            {
                if (_model == null)
                    return;

                int lineNumber = line.LineNumber;
                int lineIndex = lineNumber - 1;

                _model.ForceTokenization(lineIndex);
                var tokens = _model.GetLineTokens(lineIndex);

                if (tokens == null)
                    return;

                // stale トークンガード: 最終トークンの開始位置が行長を超えている場合はスキップ
                if (tokens.Count > 0 && tokens[tokens.Count - 1].StartIndex > line.Length)
                    return;

                tokens = new List<TMToken>(tokens);

                var transformsInLine = ArrayPool<ForegroundTextTransformation>.Shared.Rent(tokens.Count);

                try
                {
                    GetLineTransformations(lineNumber, line.Length, tokens, transformsInLine);

                    for (int i = 0; i < tokens.Count; i++)
                    {
                        if (transformsInLine[i] == null)
                            continue;

                        transformsInLine[i].Transform(this, line);
                    }
                }
                finally
                {
                    ArrayPool<ForegroundTextTransformation>.Shared.Return(transformsInLine);
                }
            }
            catch (Exception ex)
            {
                _exceptionHandler?.Invoke(ex);
            }
        }

        private void GetLineTransformations(int lineNumber, int lineLength, List<TMToken> tokens, ForegroundTextTransformation[] transformations)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var nextToken = (i + 1) < tokens.Count ? tokens[i + 1] : null;

                var startIndex = token.StartIndex;
                // DocumentSnapshot 経由ではなくドキュメント直参照の行長を使う
                var endIndex = nextToken?.StartIndex ?? lineLength;

                if (startIndex >= endIndex || token.Scopes == null || token.Scopes.Count == 0)
                {
                    transformations[i] = null;
                    continue;
                }

                var lineOffset = _document.GetLineByNumber(lineNumber).Offset;

                int foreground = 0;
                int background = 0;
                FontStyle fontStyle = 0;

                foreach (var themeRule in _theme.Match(token.Scopes))
                {
                    if (foreground == 0 && themeRule.foreground > 0)
                        foreground = themeRule.foreground;

                    if (background == 0 && themeRule.background > 0)
                        background = themeRule.background;

                    if (fontStyle == 0 && themeRule.fontStyle > 0)
                        fontStyle = themeRule.fontStyle;
                }

                if (transformations[i] == null)
                    transformations[i] = new ForegroundTextTransformation();

                transformations[i].ColorMap = _brushes;
                transformations[i].ExceptionHandler = _exceptionHandler;
                transformations[i].StartOffset = lineOffset + startIndex;
                transformations[i].EndOffset = lineOffset + endIndex;
                transformations[i].ForegroundColor = foreground;
                transformations[i].BackgroundColor = background;
                transformations[i].FontStyle = fontStyle;
            }
        }


        public void ModelTokensChanged(ModelTokensChangedEvent e)
        {
            // BG トークナイザーの Redraw を抑制。
            // TransformLine 内で ForceTokenization + ディープコピーを行うため、
            // BG スレッド起因の Redraw は不要。
            // むしろ BG Redraw が過渡的トークンでペースト時のカラーフラッシュを引き起こす。
        }

        static string NormalizeColor(string color)
        {
            if (color.Length == 9)
            {
                Span<char> normalizedColor = stackalloc char[] { '#', color[7], color[8], color[1], color[2], color[3], color[4], color[5], color[6] };

                return normalizedColor.ToString();
            }

            return color;
        }
    }
}