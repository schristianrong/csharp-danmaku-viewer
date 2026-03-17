using System;
using System.Collections.Generic;
using System.Text;

namespace LiveChatDanmakuViewer.Services
{
    /// <summary>
    /// 将文本转换为轻量字符画，用于弹幕展示模式。
    /// </summary>
    public static class AsciiArtRenderer
    {
        private const int MaxInputLines = 2;
        private const int MaxCharsPerLine = 20;

        /// <summary>
        /// 将普通文本渲染成字符画文本。
        /// </summary>
        /// <param name="text">输入文本。</param>
        /// <returns>字符画文本。</returns>
        public static string Render(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            string normalizedText = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalizedText.Split('\n');
            int renderedLineCount = Math.Min(lines.Length, MaxInputLines);

            List<string> output = new List<string>();
            for (int index = 0; index < renderedLineCount; index++)
            {
                if (index > 0)
                {
                    output.Add(string.Empty);
                }

                output.AddRange(RenderLine(lines[index]));
            }

            if (lines.Length > MaxInputLines)
            {
                output.Add("[... more lines omitted ...]");
            }

            return string.Join(Environment.NewLine, output);
        }

        /// <summary>
        /// 按字符绘制三行框体，形成稳定的字符画效果。
        /// </summary>
        private static IEnumerable<string> RenderLine(string line)
        {
            bool isTruncated = line.Length > MaxCharsPerLine;
            string content = isTruncated ? line.Substring(0, MaxCharsPerLine) : line;

            if (content.Length == 0)
            {
                return new[] { "+--+", "|__|", "+--+" };
            }

            StringBuilder top = new StringBuilder();
            StringBuilder middle = new StringBuilder();
            StringBuilder bottom = new StringBuilder();

            foreach (char rawChar in content)
            {
                char displayChar = char.IsControl(rawChar) ? '?' : rawChar;
                if (displayChar == ' ')
                {
                    displayChar = '_';
                }

                top.Append("+---");
                middle.Append("| ");
                middle.Append(displayChar);
                middle.Append(' ');
                bottom.Append("+---");
            }

            top.Append('+');
            middle.Append('|');
            bottom.Append('+');

            if (isTruncated)
            {
                return new[] { top.ToString(), middle.ToString(), bottom.ToString(), "(... truncated ...)" };
            }

            return new[] { top.ToString(), middle.ToString(), bottom.ToString() };
        }
    }
}
