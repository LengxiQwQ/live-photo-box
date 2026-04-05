using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class FeedbackService
    {
        private const string GitHubIssuesUrl = "https://github.com/LengxiQwQ/live-photo-box/issues/new";

        public static Uri GetIssuesUri()
        {
            return new Uri(GitHubIssuesUrl);
        }

        public static async Task OpenIssuePageAsync()
        {
            await FilePickerService.OpenUriAsync(GetIssuesUri());
        }
    }
}
