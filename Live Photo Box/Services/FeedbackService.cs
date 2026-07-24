using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    // 用户反馈服务 — 提供向 GitHub Issues 页面跳转的功能。
    // 应用各处的"报告问题"按钮以及崩溃对话框中的反馈链接均通过此服务打开反馈页面。
    public static class FeedbackService
    {
        private const string GitHubIssuesUrl = "https://github.com/LengxiQwQ/live-photo-box/issues/new/choose";

        // 获取 GitHub Issues 新建问题页面的 Uri。
        // è¿å: 指向 GitHub Issues 新建页面的 Uri 对象
        public static Uri GetIssuesUri()
        {
            return new Uri(GitHubIssuesUrl);
        }

        // 在默认浏览器中打开 GitHub Issues 新建问题页面。
        public static async Task OpenIssuePageAsync()
        {
            await FilePickerService.OpenUriAsync(GetIssuesUri());
        }
    }
}
