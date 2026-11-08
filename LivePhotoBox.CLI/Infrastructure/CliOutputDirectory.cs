using System;
using System.Text.Json;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Cli.Infrastructure;

internal static class CliOutputDirectory
{
    internal static bool TryEnsure(string directory, bool json, string command)
    {
        if (OutputDirectoryAttempt.Ensure(directory) is not { } failure) return true;
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { command, status = "failed", errorCode = "output_directory",
                outputDirectory = directory, reason = failure.Message }));
        else
            CliConsole.WriteErrorLine("Cannot prepare output directory: " + failure.Message);
        return false;
    }
}
