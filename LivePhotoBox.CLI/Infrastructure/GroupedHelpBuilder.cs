using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.IO;
using System.Linq;

namespace LivePhotoBox.Cli.Infrastructure
{
    /// <summary>
    /// Custom HelpBuilder that groups options into logical sections
    /// (INPUT / OUTPUT / FORMAT / EXECUTION / POST) instead of
    /// dumping them alphabetically.
    /// </summary>
    internal sealed class GroupedHelpBuilder : HelpBuilder
    {
        // Option → section label. Options not listed here go to "OTHER".
        // Order matters — sections appear in declaration order.
        private static readonly Dictionary<string, string> MergeSections = new(StringComparer.Ordinal)
        {
            ["--image"] = "INPUT",
            ["--video"] = "INPUT",
            ["--dir"] = "INPUT",
            ["--recursive"] = "INPUT",
            ["--pairing"] = "INPUT",

            ["--output"] = "OUTPUT",
            ["--preserve-subdirs"] = "OUTPUT",
            ["--overwrite"] = "OUTPUT",
            ["--after"] = "OUTPUT",

            ["--protocol"] = "FORMAT",
            ["--format"] = "FORMAT",
            ["--naming"] = "FORMAT",

            ["--parallel"] = "EXECUTION",
            ["--yes"] = "EXECUTION",
            ["--dry-run"] = "EXECUTION",
            ["--verbose"] = "EXECUTION",
        };

        public GroupedHelpBuilder(IConsole console) : base(LocalizationResources.Instance, maxWidth: 100) { }

        public override void Write(HelpContext context)
        {
            if (context.Command.Name == "merge")
            {
                WriteMergeHelp(context);
                return;
            }

            // Other commands: standard output
            base.Write(context);
        }

        private void WriteMergeHelp(HelpContext context)
        {
            // ═══ Description ═══
            if (!string.IsNullOrWhiteSpace(context.Command.Description))
            {
                // Split into lines and print manually to preserve formatting
                foreach (var line in context.Command.Description.Split('\n'))
                    context.Output.WriteLine(line.TrimEnd());
                context.Output.WriteLine();
            }

            // ═══ Usage ═══
            context.Output.WriteLine("Usage:");
            context.Output.WriteLine($"  livephotobox merge [options]");
            context.Output.WriteLine();

            // ═══ Grouped options ═══
            var options = context.Command.Options
                .Where(o => !o.IsHidden)
                .ToList();

            WriteOptionGroup(context, "═══ INPUT — what to merge ═══", options, "INPUT");
            WriteOptionGroup(context, "═══ OUTPUT — where and how to save ═══", options, "OUTPUT");
            WriteOptionGroup(context, "═══ FORMAT — protocol, container, naming ═══", options, "FORMAT");
            WriteOptionGroup(context, "═══ EXECUTION — speed, safety, logging ═══", options, "EXECUTION");

            // Remaining options
            var remaining = options
                .Where(o => Classify(o) is null)
                .ToList();
            if (remaining.Count > 0)
            {
                WriteSection(context, "═══ OTHER ═══");
                foreach (var opt in remaining)
                    WriteOption(context, opt);
                context.Output.WriteLine();
            }
        }

        private void WriteOptionGroup(HelpContext context, string header,
            List<Option> allOptions, string groupKey)
        {
            var group = allOptions
                .Where(o => Classify(o) == groupKey)
                .ToList();
            if (group.Count == 0) return;

            WriteSection(context, header);
            foreach (var opt in group)
                WriteOption(context, opt);
            context.Output.WriteLine();
        }

        private static string? Classify(Option option)
        {
            foreach (var alias in option.Aliases)
            {
                if (MergeSections.TryGetValue(alias, out var section))
                    return section;
            }
            return null;
        }

        // ── output helpers ──

        private void WriteSection(HelpContext context, string header)
        {
            if (Console.ForegroundColor != ConsoleColor.White)
                Console.ResetColor();

            // Bold-ish header via leading/trailing spaces and separators
            context.Output.WriteLine(header);
        }

        private void WriteOption(HelpContext context, Option option)
        {
            // Build alias string: "-i, --image <image>"
            var aliases = string.Join(", ", option.Aliases.OrderBy(a => a.Length));
            var typePart = option.ValueType == typeof(bool) ? "" : $" <{option.Name}>";

            // Default hint — hardcoded for readability
            string defaultHint = "";
            if (option.Aliases.Contains("--protocol")) defaultHint = " [default: v2]";
            else if (option.Aliases.Contains("--naming")) defaultHint = " [default: keep]";
            else if (option.Aliases.Contains("--parallel")) defaultHint = " [default: CPU cores]";
            else if (option.Aliases.Contains("--pairing")) defaultHint = " [default: name]";
            else if (option.Aliases.Contains("--after")) defaultHint = " [default: none]";

            var indent = "  ";
            var label = $"{indent}{aliases}{typePart}";
            var labelWidth = Math.Max(label.Length, 2);
            var padding = labelWidth < 32 ? new string(' ', 32 - labelWidth) : "  ";

            var desc = option.Description ?? "";
            var lines = desc.Split('\n');
            var firstLine = lines[0].Trim();

            // Line 1: label + description + default
            context.Output.WriteLine($"{label}{padding}{firstLine}{defaultHint}");

            // Line 2+: continuation indented under description
            var contIndent = new string(' ', 32 + 2); // align under description text
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line))
                    context.Output.WriteLine($"{contIndent}{line}");
            }
        }
    }
}
