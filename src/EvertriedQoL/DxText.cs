using Localization;

namespace EvertriedQoL
{
    /// <summary>
    /// Text overrides applied to the game's localized strings. The patcher wraps the result of
    /// LocalizedTextData.GetText(LanguageType) with <see cref="Fix"/>.
    /// </summary>
    public static class DxText
    {
        // The settings entry used to read "Gamepad Type". Input is now detected automatically and
        // this option only chooses which button glyphs are drawn.
        private const string GlyphSetKey = "ST_ControllerSelector_Text";

        public static string Fix(string text, LocalizedTextData data, LanguageType language)
        {
            if (data == null || data.name != GlyphSetKey)
            {
                return text;
            }
            switch (language)
            {
                case LanguageType.PTBR: return "Conjunto de Ícones";
                case LanguageType.JP: return "ボタン表示";
                case LanguageType.DE: return "Tastensymbole";
                case LanguageType.ES: return "Conjunto de iconos";
                case LanguageType.RU: return "Значки кнопок";
                case LanguageType.SC: return "按键图标";
                case LanguageType.KR: return "버튼 아이콘";
                case LanguageType.FR: return "Style des boutons";
                case LanguageType.TC: return "按鍵圖示";
                default: return "Glyph Set";
            }
        }
    }
}
