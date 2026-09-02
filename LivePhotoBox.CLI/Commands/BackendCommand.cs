using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.CommandLine;

namespace LivePhotoBox.Cli.Commands;

internal static class BackendCommand
{
    public static Command Create()
    {
        var command = new Command("backend", "View or configure the global Rebuilt/Legacy processing switch");
        command.SetAction(_ => Show());

        var modeValue = new Argument<string>("mode") { Description = "legacy or rebuilt" };
        var mode = new Command("mode", "Set the global processing branch") { modeValue };
        mode.SetAction(parseResult => SetMode(parseResult.GetValue(modeValue)!));

        var reset = new Command("reset", "Reset to the default Rebuilt branch");
        reset.SetAction(_ =>
        {
            ProcessingBackendSettingsService.Reset();
            Console.WriteLine("Processing pipeline reset. Mode: rebuilt (default).");
        });

        command.Add(mode);
        command.Add(reset);
        return command;
    }

    private static void Show()
    {
        ProcessingBackendSettings settings = ProcessingBackendSettingsService.Load();
        Console.WriteLine();
        CliConsole.WriteFieldRgb("Configuration", ProcessingBackendSettingsService.SettingsPath, valueColor: CliConsole.PathGreen);
        CliConsole.WriteField("Settings revision", settings.Revision.ToString(), valueColor: ConsoleColor.Yellow);
        CliConsole.WriteField("Processing mode", ProcessingBackendSettingsService.FormatMode(settings.Mode), valueColor: ConsoleColor.Yellow);
        Console.WriteLine();
        Console.WriteLine(settings.Mode == ProcessingPipelineMode.Rebuilt
            ? "Rebuilt is the default media pipeline. Standalone format conversion uses Native; protocol writers are not enabled yet."
            : "Legacy is available only when explicitly selected and retains the old protocol implementation.");
    }

    private static int SetMode(string value)
    {
        if (!ProcessingBackendSettingsService.TryParseMode(value, out ProcessingPipelineMode mode))
        {
            CliConsole.WriteErrorLine("Error: Mode must be legacy or rebuilt.");
            return 1;
        }

        ProcessingBackendSettingsService.SetMode(mode);
        Console.WriteLine($"Processing mode set to {ProcessingBackendSettingsService.FormatMode(mode)}.");
        return 0;
    }
}
