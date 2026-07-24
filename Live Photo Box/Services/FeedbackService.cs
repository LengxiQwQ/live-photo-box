using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    // User feedback service — provides navigation to the GitHub Issues page.
    // The "Report Issue" button in the app and the feedback link in crash dialogs all use this service.
    // 用户反馈服务 — 提供向 GitHub Issues 页面跳转的功能。
    // 应用各处的"报告问题"按钮以及崩溃对话框中的反馈链接均通过此服务打开反馈页面。
    public static class FeedbackService
    {
        private const string GitHubIssuesUrl = "https://github.com/LengxiQwQ/live-photo-box/issues/new/choose";

        // Returns the Uri for the GitHub Issues template chooser page.
        // 返回 GitHub Issues 模板选择页面的 Uri。
        public static Uri GetIssuesUri()
        {
            return new Uri(GitHubIssuesUrl);
        }

        // Opens the GitHub Issues template chooser page in the default browser.
        // 在默认浏览器中打开 GitHub Issues 模板选择页面。
        public static async Task OpenIssuePageAsync()
        {
            await FilePickerService.OpenUriAsync(GetIssuesUri());
        }
    }
}
