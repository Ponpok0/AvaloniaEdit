using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TextMateSharp.Themes;

namespace AvaloniaEdit.TextMate
{
    /// <summary>
    /// 既存の <see cref="IRawTheme"/> を包み、tokenColors の末尾に前景色だけのルールを追加するラッパー。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 追加先を主テーマ側にするのは <see cref="Theme.Match"/> の走査順によるもの。Match は
    /// 主テーマの Trie を全スコープ分走査してから include（dark_vs 等）を走査し、
    /// 消費側は「最初に foreground &gt; 0 になったルール」を採用するため、主テーマへ足したルールが勝つ。
    /// </para>
    /// <para>
    /// fontStyle と background を持たせないのは意図的。ルールを Trie へ挿す際の
    /// ThemeTrieElementRule.AcceptOverwrite は fontStyle が NotSet、background が 0 のものを
    /// 据え置くため、見出しの bold などの書体は元テーマのものが残る。差し替わるのは前景色だけになる。
    /// </para>
    /// </remarks>
    internal sealed class ForegroundOverrideTheme : IRawTheme
    {
        // TextMateSharp の StringUtils.IsValidHexColor は末尾がアンカーされておらず
        // "#RRGGBBAA" や "#RRGGBBzz" も 6 桁として通してしまう。前者は α が R として
        // 解釈され、後者は Color.Parse を例外化させるため、ここで厳密に弾く
        private static readonly Regex s_hex = new Regex("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly IRawTheme _inner;
        private readonly List<IRawThemeSetting> _tokenColors;

        private ForegroundOverrideTheme(IRawTheme inner, List<IRawThemeSetting> tokenColors)
        {
            _inner = inner;
            _tokenColors = tokenColors;
        }

        /// <summary>
        /// 前景色の上書きを適用したテーマを作る。適用対象が 1 件も無ければ
        /// <paramref name="inner"/> をそのまま返すので、呼び出し側は参照比較で
        /// 「上書きなし」を判定できる。
        /// </summary>
        /// <param name="inner">元になるテーマ。null なら null を返す。</param>
        /// <param name="overrides">スコープ名と "#RRGGBB" 形式の色。書式不正な色は無視する。</param>
        internal static IRawTheme Create(IRawTheme inner, IReadOnlyList<KeyValuePair<string, string>> overrides)
        {
            if (inner is null || overrides is null || overrides.Count == 0) return inner;

            var tokenColors = new List<IRawThemeSetting>();
            var baseColors = inner.GetTokenColors();
            if (baseColors != null) tokenColors.AddRange(baseColors);

            var added = 0;
            foreach (var entry in overrides)
            {
                if (string.IsNullOrEmpty(entry.Key) || entry.Value is null) continue;
                if (!s_hex.IsMatch(entry.Value)) continue;

                tokenColors.Add(new Setting(entry.Key, entry.Value));
                added++;
            }

            return added == 0 ? inner : new ForegroundOverrideTheme(inner, tokenColors);
        }

        public string GetName() => _inner.GetName();

        public string GetInclude() => _inner.GetInclude();

        public ICollection<IRawThemeSetting> GetSettings() => _inner.GetSettings();

        public ICollection<IRawThemeSetting> GetTokenColors() => _tokenColors;

        public ICollection<KeyValuePair<string, object>> GetGuiColors() => _inner.GetGuiColors();

        private sealed class Setting : IRawThemeSetting, IThemeSetting
        {
            private readonly string _scope;
            private readonly string _foreground;

            internal Setting(string scope, string foreground)
            {
                _scope = scope;
                _foreground = foreground;
            }

            public string GetName() => null;

            public object GetScope() => _scope;

            public IThemeSetting GetSetting() => this;

            // null を返すと ParsedTheme 側で FontStyle.NotSet になり、元テーマの書体が維持される
            public object GetFontStyle() => null;

            public string GetBackground() => null;

            public string GetForeground() => _foreground;
        }
    }
}
