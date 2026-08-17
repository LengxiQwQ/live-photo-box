using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /*
     * FeedbackService.cs
     *
     * 用户反馈服务。提供向 GitHub Issues 模板选择页面的跳转。
     * 应用各处的"报告问题"按钮以及崩溃对话框中的反馈链接均通过此服务打开。
     */
    public static class FeedbackService
    {
        private const string GitHubIssuesUrl = "https://github.com/LengxiQwQ/live-photo-box/issues";

        // 返回 GitHub Issues 模板选择页面的 Uri。
        public static Uri GetIssuesUri()
        {
            return new Uri(GitHubIssuesUrl);
        }

        // 在默认浏览器中打开 GitHub Issues 模板选择页面。
        public static async Task OpenIssuePageAsync()
        {
            await FilePickerService.OpenUriAsync(GetIssuesUri());
        }
    }
}
