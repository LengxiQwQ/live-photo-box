using System.Threading.Tasks;

namespace LivePhotoBox.Services;

internal static class ProcessingNotReadyDialogService
{
    public static async Task ShowAsync(string operation)
    {
        if (App.MainWindow?.Content?.XamlRoot is not { } xamlRoot)
            return;

        string operationName = operation.ToLowerInvariant() switch
        {
            "merge" => ResourceService.GetString("Processing_NotReady_Operation_Merge"),
            "split" => ResourceService.GetString("Processing_NotReady_Operation_Split"),
            "cover" => ResourceService.GetString("Processing_NotReady_Operation_Cover"),
            "repair" => ResourceService.GetString("Processing_NotReady_Operation_Repair"),
            _ => operation
        };

        await DialogService.ShowSingleAsync(
            xamlRoot,
            ResourceService.GetString("Processing_NotReady_Title"),
            ResourceService.Format("Processing_NotReady_Message", operationName),
            ResourceService.GetString("Msg_GotIt"));
    }
}
