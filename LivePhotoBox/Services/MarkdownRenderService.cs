/*
 * MarkdownRenderService.cs
 *
 * 将 Markdown 文本渲染为带样式的 HTML，供 WebView2 展示。
 * 使用 Markdig 库解析 Markdown → HTML，再包裹进包含 CSS 样式的完整 HTML 文档。
 *
 * 样式适配应用的亮/暗主题，通过 CSS 变量实现主题切换。
 * 用 WebView2.NavigateToString() 直接加载即可，无需启动本地服务器。
 */

using Markdig;
using System;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// Markdown 渲染服务。将 GitHub 风格的 Markdown 转换为可在 WebView2 中美观展示的 HTML。
    /// </summary>
    public static class MarkdownRenderService
    {
        // Markdig 解析管线（复用，避免每次创建）
        private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()   // 表格、脚注、数学公式、任务列表等
            .UseEmojiAndSmiley()       // :smile: → 😄
            .UseSoftlineBreakAsHardlineBreak()  // 单换行也换行（GitHub 风格）
            .Build();

        /// <summary>
        /// 将 Markdown 字符串渲染为独立 HTML 文档（包含完整 CSS）。
        /// 可通过 WebView2.NavigateToString() 直接加载。
        /// </summary>
        /// <param name="markdown">Markdown 源文本</param>
        /// <param name="isDarkTheme">是否使用暗色主题</param>
        /// <param name="baseUrl">相对链接的基准 URL（如 https://github.com/user/repo/blob/v1.0/）</param>
        /// <returns>完整的 HTML 文档字符串</returns>
        public static string RenderToHtml(string markdown, bool isDarkTheme = false, string? baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return WrapHtml("<p style='color:#888;text-align:center;padding:20px;'>(No release notes)</p>", isDarkTheme, baseUrl);

            // Markdig 解析 Markdown → HTML
            var bodyHtml = Markdown.ToHtml(markdown, _pipeline);

            return WrapHtml(bodyHtml, isDarkTheme, baseUrl);
        }

        /// <summary>
        /// 将 body 内容包裹进带 CSS 样式的完整 HTML 文档。
        /// CSS 参考 GitHub 的 markdown 渲染风格，适配亮/暗主题。
        /// </summary>
        private static string WrapHtml(string bodyHtml, bool isDark, string? baseUrl = null)
        {
            // 主题色变量
            var bg = isDark ? "#1e1e1e" : "#ffffff";
            var fg = isDark ? "#d4d4d4" : "#1e1e1e";
            var codeBg = isDark ? "#2d2d2d" : "#f5f5f5";
            var border = isDark ? "#404040" : "#d0d7de";
            var link = isDark ? "#6cb6ff" : "#0969da";
            var muted = isDark ? "#8b949e" : "#656d76";
            var headingFg = isDark ? "#e6e6e6" : "#1f2328";
            var blockquoteBorder = isDark ? "#444c56" : "#d0d7de";
            var blockquoteBg = isDark ? "#262626" : "#f6f8fa";

            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
{(string.IsNullOrEmpty(baseUrl) ? "" : $"<base href='{baseUrl}'>")}
<style>
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  body {{
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Microsoft YaHei UI', sans-serif;
    font-size: 13px;
    line-height: 1.6;
    color: {fg};
    background: {bg};
    padding: 8px 4px;
    word-wrap: break-word;
    overflow-wrap: break-word;
  }}
  h1, h2, h3, h4, h5, h6 {{
    margin-top: 16px;
    margin-bottom: 8px;
    font-weight: 600;
    line-height: 1.35;
    color: {headingFg};
  }}
  h1 {{ font-size: 1.4em; border-bottom: 1px solid {border}; padding-bottom: 6px; }}
  h2 {{ font-size: 1.2em; border-bottom: 1px solid {border}; padding-bottom: 5px; }}
  h3 {{ font-size: 1.05em; }}
  p {{ margin-bottom: 8px; }}
  ul, ol {{ padding-left: 24px; margin-bottom: 8px; }}
  li {{ margin-bottom: 2px; }}
  li > p {{ margin-bottom: 0; }}
  code {{
    font-family: 'Cascadia Code', 'Consolas', 'Courier New', monospace;
    font-size: 0.9em;
    background: {codeBg};
    padding: 1px 5px;
    border-radius: 4px;
    border: 1px solid {border};
  }}
  pre {{
    background: {codeBg};
    border: 1px solid {border};
    border-radius: 6px;
    padding: 12px;
    margin-bottom: 8px;
    overflow-x: auto;
    line-height: 1.45;
  }}
  pre > code {{
    background: none;
    border: none;
    padding: 0;
    font-size: 0.85em;
  }}
  blockquote {{
    border-left: 3px solid {blockquoteBorder};
    background: {blockquoteBg};
    padding: 6px 12px;
    margin-bottom: 8px;
    color: {muted};
  }}
  blockquote > :last-child {{ margin-bottom:0; }}
  a {{ color: {link}; text-decoration: none; }}
  a:hover {{ text-decoration: underline; }}
  table {{
    border-collapse: collapse;
    width: 100%;
    margin-bottom: 8px;
  }}
  th, td {{
    border: 1px solid {border};
    padding: 6px 10px;
    text-align: left;
  }}
  th {{ background: {codeBg}; font-weight: 600; }}
  img {{ max-width: 100%; height: auto; border-radius: 4px; }}
  hr {{
    border: none;
    border-top: 1px solid {border};
    margin: 12px 0;
  }}
  strong {{ font-weight: 600; }}
  /* 任务列表 */
  .task-list-item {{ list-style: none; }}
  .task-list-item input {{ margin-right: 6px; }}
  /* 首尾去多余间距 */
  body > :first-child {{ margin-top: 0 !important; }}
  body > :last-child {{ margin-bottom: 0 !important; }}
</style>
</head>
<body>{bodyHtml}</body>
</html>";
        }
    }
}
