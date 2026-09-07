// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Utils;
using ITextSource = Avalonia.Media.TextFormatting.ITextSource;

namespace AvaloniaEdit.Rendering
{
	/// <summary>
	/// WPF TextSource implementation that creates TextRuns for a VisualLine.
	/// </summary>
	internal sealed class VisualLineTextSource : ITextSource, ITextRunConstructionContext
	{
		public VisualLineTextSource(VisualLine visualLine)
		{
			VisualLine = visualLine;
		}

		public VisualLine VisualLine { get; private set; }
		public TextView TextView { get; set; }
		public TextDocument Document { get; set; }
		public TextRunProperties GlobalTextRunProperties { get; set; }

		/// <summary>
		/// 選択前景色版の TextLine を追加生成する 2 周目のフォーマットで true にする。
		/// InlineObjectRun は 1 周目で既に TextView へ登録済みのため、
		/// 2 周目で再登録すると同じ Control が二重に配置される。
		/// </summary>
		public bool SuppressInlineObjectRegistration { get; set; }

		/// <summary>
		/// 非 null のとき、返す TextRun の前景色をこの色で差し替える。
		/// 選択前景色版の TextLine を作る 2 周目のフォーマットで設定する。
		/// </summary>
		/// <remarks>
		/// 要素の <see cref="VisualLineElementTextRunProperties"/> を書き換えて整形し、
		/// 後で元に戻す方式は使えない。整形済みの TextRun はそのインスタンスを参照で
		/// 保持しており、色を戻すと生成済みの TextLine まで元の色に戻ってしまう
		/// （通常版と選択色版が 1 個のプロパティを共有することになる）。
		/// run ごとに専用のプロパティを作れば、2 つの TextLine セットは互いに
		/// 独立した色を持てる。
		/// </remarks>
		public IBrush SelectionForegroundOverride { get; set; }

		public TextRun GetTextRun(int textSourceCharacterIndex)
		{
			try {
				foreach (VisualLineElement element in VisualLine.Elements) {
					if (textSourceCharacterIndex >= element.VisualColumn
						&& textSourceCharacterIndex < element.VisualColumn + element.VisualLength) {
						int relativeOffset = textSourceCharacterIndex - element.VisualColumn;
						TextRun run = element.CreateTextRun(textSourceCharacterIndex, this);
						if (run == null)
							throw new ArgumentNullException(element.GetType().Name + ".CreateTextRun");
						if (run.Length == 0)
							throw new ArgumentException("The returned TextRun must not have length 0.", element.GetType().Name + ".Length");
						if (relativeOffset + run.Length > element.VisualLength)
							throw new ArgumentException("The returned TextRun is too long.", element.GetType().Name + ".CreateTextRun");
						if (run is InlineObjectRun inlineRun && !SuppressInlineObjectRegistration) {
							inlineRun.VisualLine = VisualLine;
							VisualLine.HasInlineObjects = true;
							TextView.AddInlineObject(inlineRun);
						}
						return ApplySelectionForeground(run);
					}
				}
				if (TextView.Options.ShowEndOfLine && textSourceCharacterIndex == VisualLine.VisualLength) {
					return CreateTextRunForNewLine();
				}
				return new TextEndOfParagraph(1);
			} catch (Exception ex) {
				Debug.WriteLine(ex.ToString());
				throw;
			}
		}

        /// <summary>
        /// <see cref="SelectionForegroundOverride"/> が設定されていれば、前景色だけを
        /// 差し替えた同内容の run を作り直して返す。
        /// </summary>
        /// <remarks>
        /// 差し替えられるのは <see cref="TextCharacters"/> だけ。整形済みテキストを描く
        /// 要素（折り畳みマーカー等）とインラインオブジェクトは自前で描画するため
        /// 前景色を外から与えられず、素通しにする。
        /// </remarks>
        private TextRun ApplySelectionForeground(TextRun run)
        {
            if (SelectionForegroundOverride == null || run is not TextCharacters characters)
                return run;

            var source = characters.Properties;
            var recolored = new GenericTextRunProperties(
                source.Typeface,
                source.FontRenderingEmSize,
                source.TextDecorations,
                SelectionForegroundOverride,
                source.BackgroundBrush,
                source.BaselineAlignment,
                source.CultureInfo,
                source.FontFeatures);

            return new TextCharacters(characters.Text, recolored);
        }

        private TextRun CreateTextRunForNewLine()
        {
            string newlineText = "";
            DocumentLine lastDocumentLine = VisualLine.LastDocumentLine;
            if (lastDocumentLine.DelimiterLength == 2)
            {
                newlineText = TextView.Options.EndOfLineCRLFGlyph;
            }
            else if (lastDocumentLine.DelimiterLength == 1)
            {
                char newlineChar = Document.GetCharAt(lastDocumentLine.Offset + lastDocumentLine.Length);
                if (newlineChar == '\r')
                    newlineText = TextView.Options.EndOfLineCRGlyph;
                else if (newlineChar == '\n')
                    newlineText = TextView.Options.EndOfLineLFGlyph;
                else
                    newlineText = "?";
            }

            var p = new VisualLineElementTextRunProperties(GlobalTextRunProperties);
            p.SetForegroundBrush(TextView.NonPrintableCharacterBrush);
            p.SetFontRenderingEmSize(GlobalTextRunProperties.FontRenderingEmSize - 2);
            var textElement = new FormattedTextElement(TextView.CachedElements.GetTextForNonPrintableCharacter(newlineText, p), 0);

            textElement.RelativeTextOffset = lastDocumentLine.Offset + lastDocumentLine.Length;

            return new FormattedTextRun(textElement, GlobalTextRunProperties);
        }

        public ReadOnlyMemory<char> GetPrecedingText(int textSourceCharacterIndexLimit)
		{
			try {
				foreach (VisualLineElement element in VisualLine.Elements) {
					if (textSourceCharacterIndexLimit > element.VisualColumn
						&& textSourceCharacterIndexLimit <= element.VisualColumn + element.VisualLength) {
						var span = element.GetPrecedingText(textSourceCharacterIndexLimit, this);
						if (span.IsEmpty)
							break;
						int relativeOffset = textSourceCharacterIndexLimit - element.VisualColumn;
						if (span.Length > relativeOffset)
							throw new ArgumentException("The returned TextSpan is too long.", element.GetType().Name + ".GetPrecedingText");
						return span;
					}
				}
				
				return ReadOnlyMemory<char>.Empty;
			} catch (Exception ex) {
				Debug.WriteLine(ex.ToString());
				throw;
			}
		}

		private string _cachedString;
		private int _cachedStringOffset;

		public StringSegment GetText(int offset, int length)
		{
			if (_cachedString != null) {
				if (offset >= _cachedStringOffset && offset + length <= _cachedStringOffset + _cachedString.Length) {
					return new StringSegment(_cachedString, offset - _cachedStringOffset, length);
				}
			}
			_cachedStringOffset = offset;
			return new StringSegment(_cachedString = Document.GetText(offset, length));
		}
	}
}
