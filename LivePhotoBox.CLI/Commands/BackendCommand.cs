using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.CommandLine;

namespace LivePhotoBox.Cli.Commands
{
    internal static class BackendCommand
    {
        public static Command Create()
        {
            var command = new Command("backend", "View or configure the processing backend");
            command.SetAction(_ => Show());

            var modeValue = new Argument<string>("mode") { Description = "auto, legacy, or custom" };
            var mode = new Command("mode", "Set the global backend mode") { modeValue };
            mode.SetAction(parseResult => SetMode(parseResult.GetValue(modeValue)!));

            var protocolName = new Argument<string>("protocol") { Description = "Protocol key or alias" };
            var engineName = new Argument<string>("engine") { Description = "legacy or native" };
            var protocol = new Command("protocol", "Set one protocol backend preference")
            {
                protocolName,
                engineName
            };
            protocol.SetAction(parseResult => SetProtocol(
                parseResult.GetValue(protocolName)!,
                parseResult.GetValue(engineName)!));

            var reset = new Command("reset", "Reset backend settings to safe defaults");
            reset.SetAction(_ =>
            {
                ProcessingBackendSettingsService.Reset();
                Console.WriteLine("Backend settings reset. Mode: auto; protocols: legacy until Native is stable.");
            });

            command.Add(mode);
            command.Add(protocol);
            command.Add(reset);
            return command;
        }

        private static void Show()
        {
            ProcessingBackendSettings settings = ProcessingBackendSettingsService.Load();
            NativeRuntimeInfo runtime = NativeRuntime.Probe();
            Console.WriteLine();
            CliConsole.WriteField("Mode", ProcessingBackendSettingsService.FormatMode(settings.Mode), valueColor: ConsoleColor.Yellow);
            CliConsole.WriteFieldRgb("Configuration", ProcessingBackendSettingsService.SettingsPath, valueColor: CliConsole.PathGreen);
            CliConsole.WriteField("Native runtime", runtime.IsAvailable ? runtime.Version ?? "available" : "unavailable", valueColor: ConsoleColor.Yellow);
            Console.WriteLine();
            Console.WriteLine($"{"Protocol",-24} {"Preference",-11} {"Native status"}");
            Console.WriteLine(new string('─', 58));
            foreach (ProcessingBackendProtocolDefinition definition in ProcessingBackendProtocolCatalog.All)
            {
                NativeBackendMaturity maturity = ProcessingBackendSettingsService.GetNativeMaturity(definition, runtime);
                Console.WriteLine($"{definition.Key,-24} {ProcessingBackendSettingsService.FormatPreference(settings.GetPreference(definition.Key)),-11} {maturity.ToString().ToLowerInvariant()}");
            }
            Console.WriteLine();
            Console.WriteLine("Auto selects only stable Native implementations and safely falls back to Legacy.");
        }

        private static int SetMode(string value)
        {
            if (!ProcessingBackendSettingsService.TryParseMode(value, out ProcessingBackendMode mode))
            {
                CliConsole.WriteErrorLine("Error: Mode must be auto, legacy, or custom.");
                return 1;
            }

            ProcessingBackendSettingsService.SetMode(mode);
            Console.WriteLine($"Backend mode set to {ProcessingBackendSettingsService.FormatMode(mode)}.");
            return 0;
        }

        private static int SetProtocol(string protocolValue, string engineValue)
        {
            if (!ProcessingBackendProtocolCatalog.TryResolve(protocolValue, out ProcessingBackendProtocolDefinition? definition)
                || definition == null)
            {
                CliConsole.WriteErrorLine($"Error: Unknown protocol '{protocolValue}'. Run 'lpb backend' to list protocol keys.");
                return 1;
            }

            ProcessingBackendPreference preference;
            if (engineValue.Equals("legacy", StringComparison.OrdinalIgnoreCase))
            {
                preference = ProcessingBackendPreference.Legacy;
            }
            else if (engineValue.Equals("native", StringComparison.OrdinalIgnoreCase))
            {
                if (ProcessingBackendSettingsService.GetNativeMaturity(definition) == NativeBackendMaturity.Unavailable)
                {
                    CliConsole.WriteErrorLine($"Error: Native backend for '{definition.Key}' is not available in this build.");
                    return 1;
                }
                preference = ProcessingBackendPreference.PreferNative;
            }
            else
            {
                CliConsole.WriteErrorLine("Error: Engine must be legacy or native.");
                return 1;
            }

            ProcessingBackendSettings settings = ProcessingBackendSettingsService.Load();
            settings.Mode = ProcessingBackendMode.Custom;
            settings.Protocols[definition.Key] = preference;
            ProcessingBackendSettingsService.Save(settings);
            Console.WriteLine($"{definition.Key} set to {ProcessingBackendSettingsService.FormatPreference(preference)}; mode set to custom.");
            return 0;
        }
    }
}
