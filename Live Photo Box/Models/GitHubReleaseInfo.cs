/*
 * GitHubReleaseInfo.cs
 *
 * GitHub Releases API 响应的 JSON 反序列化模型。
 * 仅定义自动更新功能需要的字段，其余字段由 JSON 序列化器自动忽略。
 *
 * API 端点：GET https://api.github.com/repos/LengxiQwQ/live-photo-box/releases/latest
 *
 * 使用方式：
 *   var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(json);
 */

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LivePhotoBox.Models
{
    /// <summary>
    /// GitHub Release 顶层响应。对应 /releases/latest API 的 JSON 结构。
    /// 只反序列化自动更新功能需要的字段。
    /// </summary>
    public class GitHubReleaseResponse
    {
        /// <summary>版本标签，如 "v1.14.11"</summary>
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        /// <summary>Release 标题，如 "Live Photo Box 1.14.11"</summary>
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        /// <summary>Release 页面 HTML 地址</summary>
        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        /// <summary>发布说明正文（Markdown 格式）</summary>
        [JsonPropertyName("body")]
        public string Body { get; init; } = string.Empty;

        /// <summary>是否为预发布版本（Prerelease）</summary>
        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        /// <summary>发布资产列表（zip、exe 等附件）</summary>
        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; init; } = new();
    }

    /// <summary>
    /// GitHub Release 中的单个资产（附件）。
    /// 每个 Release 通常包含 portable.zip 和 setup.exe 两个资产。
    /// </summary>
    public class GitHubAsset
    {
        /// <summary>文件名，如 "Live-Photo-Box-v1.14.11-x64-portable.zip"</summary>
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        /// <summary>下载直链 URL</summary>
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        /// <summary>文件大小（字节）</summary>
        [JsonPropertyName("size")]
        public long Size { get; init; }

        /// <summary>MIME 类型，如 "application/zip"、"application/x-msdownload"</summary>
        [JsonPropertyName("content_type")]
        public string ContentType { get; init; } = string.Empty;
    }
}
