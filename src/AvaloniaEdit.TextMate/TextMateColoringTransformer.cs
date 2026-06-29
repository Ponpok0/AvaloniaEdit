using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using TextMateSharp.Grammars;
using TextMateSharp.Model;
using TextMateSharp.Themes;
using FontStyle = TextMateSharp.Themes.FontStyle;

namespace AvaloniaEdit.TextMate
{
    public class TextMateColoringTransformer :
        GenericLineTransformer,
        IModelTokensChangedListener,
        IDisposable
    {
        private readonly object _lock = new object();
        private bool _isDisposed;
        private Theme _theme;
        private IGrammar _grammar;
        private TMModel _model;
        private TextDocument _document;
        private readonly TextView _textView;
        private readonly Action<Exception> _exceptionHandler;

        // VisualLinesChanged トラッキング (_areVisualLinesValid / _firstVisibleLineIndex /
        // _lastVisibleLineIndex) は ModelTokensChanged の Redraw 制御専用だったが、
        // ペーストフラッシュ対策で ModelTokensChanged を no-op 化したため削除済み。

        // Copy-on-write: SetTheme builds a new dictionary and atomically swaps
        // the reference under lock. Readers capture the reference once and use it
        // safely - the captured dictionary is never mutated after publication.
        // Dictionary.Clear() is intentionally NOT called anywhere because an
        // in-flight TransformLine may hold a captured local reference to the
        // same dictionary object. Mutating it in-place via Clear() would corrupt
        // the concurrent read. The old dictionary becomes unreachable once all
        // in-flight readers complete, and is collected by GC naturally.
        private Dictionary<int, IBrush> _brushes;

        /// <summary>
        /// トークンのスコープリスト・行テキスト・トークン範囲を受け取り、
        /// true を返した場合はそのトークンの着色をスキップする。
        /// 引数: (scopes, lineText, startIndex, endIndex)
        /// </summary>
        public Func<List<string>, string, int, int, bool> ScopeFilter { get; set; }

        /// <summary>
        /// Initializes a new instance of the TextMateColoringTransformer class, which applies syntax highlighting to
        /// the specified text view.
        /// </summary>
        /// <remarks>The TextMateColoringTransformer subscribes to the VisualLinesChanged event of the
        /// TextView to update the syntax highlighting when the visual lines change.</remarks>
        /// <param name="textView">The TextView instance to which syntax highlighting will be applied. This parameter cannot be null.</param>
        /// <param name="exceptionHandler">An action to handle exceptions that occur during the transformation process.</param>
        /// <exception cref="ArgumentNullException">Thrown if the textView parameter is null.</exception>
        public TextMateColoringTransformer(
            TextView textView,
            Action<Exception> exceptionHandler)
            : base(exceptionHandler)
        {
            _textView = textView ?? throw new ArgumentNullException(nameof(textView));
            _exceptionHandler = exceptionHandler;

            _brushes = new Dictionary<int, IBrush>();
        }

        /// <summary>
        /// Associates the specified text document and model with the transformer for processing and coloring.
        /// </summary>
        /// <remarks>This method is thread-safe and locks access during the operation. It validates the
        /// state of the model and grammar before setting them, ensuring that stale references are severed when null
        /// values are provided.</remarks>
        /// <param name="document">The text document to be associated with the transformer, or <see langword="null"/> to clear the existing document reference (e.g., during disposal).</param>
        /// <param name="model">The model to be set for the document, or <see langword="null"/> to clear the existing model reference (e.g., during disposal).
        /// When both <paramref name="document"/> and <paramref name="model"/> are null, all stale references are severed.</param>
        public void SetModel(TextDocument document, TMModel model)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                ThrowIfDisposed();

                _document = document;
                _model = model;

                // Null guard: prevents NRE when model is null (e.g., during
                // Installation.Dispose teardown). This also enables Installation
                // to safely call SetModel(null, null) to sever stale references.
                if (_grammar != null && _model != null)
                {
                    _model.SetGrammar(_grammar);
                }
            }
        }

        /// <summary>
        /// Releases all resources used by this <see cref="TextMateColoringTransformer"/> instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by this <see cref="TextMateColoringTransformer"/>
        /// instance and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> to release both managed and unmanaged resources;
        /// <c>false</c> to release only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            // Fast path: Volatile.Read avoids lock acquisition when already disposed.
            if (Volatile.Read(ref _isDisposed))
                return;

            if (!disposing)
                return;

            lock (_lock)
            {
                // Authoritative check under lock.
                if (Volatile.Read(ref _isDisposed))
                    return;

                // Volatile.Write ensures that Volatile.Read callers outside the lock
                // (ThrowIfDisposed, fast-path guards) see this write with proper
                // memory ordering. Both sides use the Volatile API to satisfy
                // analyzers that require matching synchronization primitives.
                Volatile.Write(ref _isDisposed, true);

                _theme = null;
                _grammar = null;
                _model = null;
                _document = null;
                _brushes = null;
            }
        }

        /// <summary>
        /// Sets the current theme for syntax highlighting by updating the internal brush dictionary with colors defined
        /// in the specified theme.
        /// </summary>
        /// <remarks>This method is thread-safe and minimizes lock contention by performing expensive
        /// brush creation operations outside the lock. If the object has been disposed, an exception is thrown. Any
        /// ongoing line transformation operations will continue to use the previous brush dictionary safely, as
        /// dictionaries are replaced atomically and never mutated.</remarks>
        /// <param name="theme">The theme to apply. This parameter provides color definitions that are used to construct the brush
        /// dictionary for syntax highlighting. Cannot be null.</param>
        public void SetTheme(Theme theme)
        {
            ThrowIfDisposed();

            // Build the new brush dictionary outside the lock. Color parsing and
            // ImmutableSolidColorBrush creation are the expensive operations - doing
            // them outside minimizes lock hold time
            var map = theme.GetColorMap();
            var newBrushes = new Dictionary<int, IBrush>();

            foreach (var color in map)
            {
                var id = theme.GetColorId(color);
                newBrushes[id] = new ImmutableSolidColorBrush(Color.Parse(NormalizeColor(color)));
            }

            lock (_lock)
            {
                ThrowIfDisposed();

                _theme = theme;

                // Atomic reference swap. Any concurrent TransformLine call that
                // already captured the old dictionary continues using it safely -
                // the old dictionary is never mutated, only replaced.
                _brushes = newBrushes;
            }
        }

        /// <summary>
        /// Sets the grammar to be used by the model, updating its internal state accordingly.
        /// </summary>
        /// <remarks>This method is thread-safe and should only be called when the object has not been
        /// disposed. If the model is already initialized, calling this method will also update the model's
        /// grammar.</remarks>
        /// <param name="grammar">The grammar to apply to the model. Determines how the model tokenizes and interprets text.
        /// If <see langword="null"/>, the current grammar reference is cleared and no grammar is applied to the model.</param>
        public void SetGrammar(IGrammar grammar)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                ThrowIfDisposed();

                _grammar = grammar;

                if (_model != null)
                {
                    _model.SetGrammar(grammar);
                }
            }
        }

        /// <summary>
        /// 指定スコープスタックに一致するテーマ前景色ブラシを返す。
        /// 一致しない場合は null。スレッドセーフ (_theme/_brushes のスナップショットを lock 内で取得)。
        /// </summary>
        public IBrush ResolveScopeBrush(List<string> scopes)
        {
            if (Volatile.Read(ref _isDisposed))
                return null;

            Theme theme;
            Dictionary<int, IBrush> brushes;
            lock (_lock)
            {
                if (Volatile.Read(ref _isDisposed))
                    return null;
                theme = _theme;
                brushes = _brushes;
            }

            if (theme == null || brushes == null)
                return null;

            foreach (var themeRule in theme.Match(scopes))
            {
                if (themeRule.foreground > 0 && brushes.TryGetValue(themeRule.foreground, out var brush))
                    return brush;
            }

            return null;
        }

        /// <summary>
        /// Transforms the specified document line by applying syntax highlighting and theme-based color transformations
        /// according to the current model and theme settings.
        /// </summary>
        /// <remarks>This method is thread-safe and ensures that transformations are only applied when the
        /// object has not been disposed. It captures necessary state under a lock to prevent race conditions and uses
        /// only local copies of mutable fields during transformation.</remarks>
        /// <param name="line">The document line to be transformed. Contains the text and formatting information for a single line in the
        /// document.</param>
        /// <param name="context">The context for text run construction, providing additional information required for rendering the line.</param>
        protected override void TransformLine(DocumentLine line, ITextRunConstructionContext context)
        {
            // Rendering callback - silently return if disposed.
            if (Volatile.Read(ref _isDisposed))
                return;

            try
            {
                // Capture field snapshots under lock. The lock acquisition is brief -
                // just 4 reference copies - and the lock is almost never contended
                // (only the background tokenizer thread competes via ModelTokensChanged).
                TMModel model;
                TextDocument document;
                Theme theme;
                Dictionary<int, IBrush> brushes;

                lock (_lock)
                {
                    if (Volatile.Read(ref _isDisposed))
                        return;

                    model = _model;
                    document = _document;
                    theme = _theme;
                    brushes = _brushes;
                }

                // All work below uses only captured locals - no mutable field reads
                if (model == null || document == null || theme == null || brushes == null)
                    return;

                int lineNumber = line.LineNumber;
                int lineIndex = lineNumber - 1;

                // 同期 ForceTokenization: BG トークナイザーの過渡的トークンを使わず常に最新を取得。
                // これにより ModelTokensChanged 経由の Redraw を no-op にしてもカラーフラッシュが起きない。
                model.ForceTokenization(lineIndex);
                var tokens = model.GetLineTokens(lineIndex);

                // If there are no tokens to process, avoid the overhead of GetLineTransformations
                // (including its internal GetLineByNumber call)
                if (tokens == null || tokens.Count == 0)
                    return;

                // stale トークンガード: 最終トークンの開始位置が行長を超えている場合はスキップ
                if (tokens[tokens.Count - 1].StartIndex > line.Length)
                    return;

                // DeepCopy: tokens リストは BG スレッドから変更される可能性があるためコピーを使う
                tokens = new List<TMToken>(tokens);

                var transformsInLine = ArrayPool<ForegroundTextTransformation>.Shared.Rent(tokens.Count);

                try
                {
                    GetLineTransformations(lineNumber, line.Length, tokens, transformsInLine, model, document, theme, brushes);

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

        /// <summary>
        /// Generates visual transformation data for each token in the specified line, applying theme-based styling to
        /// enable syntax highlighting and formatting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// All dependencies are passed as parameters - this method performs zero
        /// mutable field reads, making it inherently thread-safe.
        /// </para>
        /// <para>
        /// This method is typically used in syntax highlighting scenarios to apply consistent
        /// visual styles to code elements. The transformations array must be pre-allocated and have the same length as
        /// the tokens list. If a token cannot be styled, its corresponding transformation entry will be set to
        /// null.
        /// </para>
        /// </remarks>
        /// <param name="lineNumber">The index of the line in the document for which transformations are to be generated.</param>
        /// <param name="tokens">A list of tokens representing the syntax elements in the specified line. Each token will be styled according
        /// to the theme.</param>
        /// <param name="transformations">An array that will be populated with transformation data for each token, defining how the tokens should be
        /// rendered visually.</param>
        /// <param name="model">The model representing the overall structure of the document, used to retrieve line and token information.</param>
        /// <param name="document">The text document containing the lines and tokens, providing access to line offsets and other document
        /// properties.</param>
        /// <param name="theme">The theme containing styling rules that dictate how tokens should be visually represented based on their
        /// scopes.</param>
        /// <param name="brushes">A dictionary mapping color identifiers to brush objects, used to apply foreground and background colors to
        /// tokens.</param>
        private void GetLineTransformations(
            int lineNumber,
            int lineLength,
            List<TMToken> tokens,
            ForegroundTextTransformation[] transformations,
            TMModel model,
            TextDocument document,
            Theme theme,
            Dictionary<int, IBrush> brushes)
        {
            // Hoisted outside the loop: lineNumber is invariant across all tokens
            // in this line, so GetLineByNumber only needs to be called once
            var docLine = document.GetLineByNumber(lineNumber);
            var lineOffset = docLine.Offset;

            // スコープフィルタ使用時のみ行テキストを取得 (1 行につき 1 回)
            string lineText = null;
            var scopeFilter = ScopeFilter;
            if (scopeFilter != null)
            {
                lineText = document.GetText(lineOffset, docLine.Length);
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var nextToken = (i + 1) < tokens.Count ? tokens[i + 1] : null;

                var startIndex = token.StartIndex;
                // DocumentSnapshot 経由ではなくドキュメント直参照の行長を使う (cc153cc 整合)
                var endIndex = nextToken?.StartIndex ?? lineLength;

                if (startIndex >= endIndex || token.Scopes == null || token.Scopes.Count == 0)
                {
                    transformations[i] = null;
                    continue;
                }

                // スコープフィルタ: 条件に合致するトークンは着色をスキップ
                if (lineText != null && scopeFilter(token.Scopes, lineText, startIndex, endIndex))
                {
                    transformations[i] = null;
                    continue;
                }

                int foreground = 0;
                int background = 0;
                FontStyle fontStyle = 0;

                foreach (var themeRule in theme.Match(token.Scopes))
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

                transformations[i].ColorMap = brushes;
                transformations[i].ExceptionHandler = _exceptionHandler;
                transformations[i].StartOffset = lineOffset + startIndex;
                transformations[i].EndOffset = lineOffset + endIndex;
                transformations[i].ForegroundColor = foreground;
                transformations[i].BackgroundColor = background;
                transformations[i].FontStyle = fontStyle;
            }
        }

        /// <summary>
        /// Handles changes to the model's token ranges and updates the visible lines in the text view as needed.
        /// </summary>
        /// <remarks>This method is invoked from a background thread and synchronizes access to shared
        /// state with the UI thread. If the model or document is disposed, or if the changed lines are not currently
        /// visible, the method returns without updating the UI.</remarks>
        /// <param name="e">An event object containing the ranges of lines in the model that have changed.</param>
        public void ModelTokensChanged(ModelTokensChangedEvent e)
        {
            // ペーストフラッシュ対策: BG トークナイザー起因の Redraw を抑制。
            // TransformLine 内で ForceTokenization + DeepCopy を行うため、
            // BG スレッド起因の Redraw は不要。
            // むしろ BG Redraw が過渡的トークンでペースト時のカラーフラッシュを引き起こす。
        }

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> if this instance has been disposed.
        /// Uses <see cref="Volatile.Read(ref bool)"/> for a lock-free memory-barrier-safe
        /// read paired with <see cref="Volatile.Write(ref bool, bool)"/> in
        /// <see cref="Dispose(bool)"/>, suitable as a fast-path guard before acquiring
        /// <see cref="_lock"/>.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _isDisposed))
                throw new ObjectDisposedException(nameof(TextMateColoringTransformer));
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
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