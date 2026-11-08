namespace LivePhotoBox.Services;

internal static class ScanSettingsMigration
{
    internal static int MapMerge(int old) => old is >= 0 and <= 2 ? old : 0;
    internal static int MapSplit(int old) => old is >= 2 and <= 7 ? old : 0;
    internal static int MergeChoice()
    {
        if (AppSettingsService.GetValue("MergeScanSelectionVersion", 0) < 2)
        {
            AppSettingsService.SetValue("PairingMethodIndex", MapMerge(AppSettingsService.GetValue("PairingMethodIndex", 0)));
            AppSettingsService.SetValue("MergeScanSelectionVersion", 2);
        }
        return MapMerge(AppSettingsService.GetValue("PairingMethodIndex", 0));
    }
    internal static int SplitChoice()
    {
        if (AppSettingsService.GetValue("SplitScanSelectionVersion", 0) < 2)
        {
            AppSettingsService.SetValue("SplitMatchProtocolIndex", MapSplit(AppSettingsService.GetValue("SplitMatchProtocolIndex", 0)));
            AppSettingsService.SetValue("SplitScanSelectionVersion", 2);
        }
        return MapSplit(AppSettingsService.GetValue("SplitMatchProtocolIndex", 0));
    }
}
