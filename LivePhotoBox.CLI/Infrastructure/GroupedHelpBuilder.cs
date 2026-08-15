using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.IO;
using System.IO;
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

        // Option → section label for the repair command. Options not listed here go to "OTHER".
        private static readonly Dictionary<string, string> RepairSections = new(StringComparer.Ordinal)
        {
            ["--dir"] = "INPUT",
            ["--recursive"] = "INPUT",

            ["--no-rotate"] = "FIX",
            ["--no-thumbnail"] = "FIX",
            ["--no-heic"] = "FIX",
            ["--no-video"] = "FIX",
            ["--all-devices"] = "FIX",
            ["--repair-long-videos"] = "FIX",
            ["--copy-perfect"] = "FIX",

            ["--output"] = "OUTPUT",
            ["--preserve-subdirs"] = "OUTPUT",
            ["--overwrite"] = "OUTPUT",

            ["--parallel"] = "EXECUTION",
            ["--yes"] = "EXECUTION",
            ["--dry-run"] = "EXECUTION",
            ["--verbose"] = "EXECUTION",
        };

        // A grouped-help section: key + human-readable header.
        private sealed record GroupHeader(string Key, string Title);

        private static readonly GroupHeader[] MergeGroupHeaders =
        {
            new("INPUT", "═══ INPUT — what to merge ═══"),
            new("OUTPUT", "═══ OUTPUT — where and how to save ═══"),
            new("FORMAT", "═══ FORMAT — protocol, container, naming ═══"),
            new("EXECUTION", "═══ EXECUTION — speed, safety, logging ═══"),
        };

        private static readonly GroupHeader[] RepairGroupHeaders =
        {
            new("INPUT", "═══ INPUT — what to scan ═══"),
            new("FIX", "═══ FIX — what to repair ═══"),
            new("OUTPUT", "═══ OUTPUT — where and how to save ═══"),
            new("EXECUTION", "═══ EXECUTION — speed, safety, logging ═══"),
        };

        public GroupedHelpBuilder(IConsole console) : base(LocalizationResources.Instance, maxWidth: 100) { }

        public override void Write(HelpContext context)
        {
            if (context.Command.Name == "merge")
            {
                WriteGroupedHelp(context, MergeSections, MergeGroupHeaders);
                return;
            }

            if (context.Command.Name == "repair")
            {
                WriteGroupedHelp(context, RepairSections, RepairGroupHeaders);
                return;
            }

            // Other commands: standard help, colored with the same design language
            WriteStandardHelp(context);
        }

        // ─── standard layout for all non-merge commands ───
        private void WriteStandardHelp(HelpContext context)
        {
            var output = context.Output;

            // Description
            if (!string.IsNullOrWhiteSpace(context.Command.Description))
            {
                WriteHeading(output, "Description:");
                output.WriteLine();
                foreach (var line in context.Command.Description.Split('\n'))
                {
                    var text = line.TrimEnd();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        output.WriteLine("  ");
                        continue;
                    }

                    WriteDescriptionLine(output, "  " + text);
                    output.WriteLine();
                }
                output.WriteLine();
            }

            // Usage
            WriteHeading(output, "Usage:");
            output.WriteLine();
            WriteUsageLine(output, GetUsageLine(context.Command));
            output.WriteLine();

            // Options
            var options = context.Command.Options.Where(o => !o.IsHidden).ToList();
            var rows = options.Select(o => GetTwoColumnRow(o, context)).ToList();

            // System.CommandLine beta4 attaches the global help option to the
            // root command; subcommand.Options does not enumerate it, so render
            // it explicitly for every command that does not already have it.
            if (!options.Any(o => o.Aliases.Contains("--help")))
                rows.Add(new TwoColumnHelpRow("-?, -h, --help", "Show help and usage information"));

            if (rows.Count > 0)
            {
                WriteHeading(output, "Options:");
                output.WriteLine();
                WriteRows(output, rows);
                output.WriteLine();
            }

            // Commands
            var subcommands = context.Command.Subcommands.Where(c => !c.IsHidden).ToList();
            if (subcommands.Count > 0)
            {
                WriteHeading(output, "Commands:");
                output.WriteLine();
                WriteRows(output, subcommands.Select(c => GetTwoColumnRow(c, context)).ToList());
                output.WriteLine();
            }
        }

        private static string GetUsageLine(Command command)
        {
            var parts = new List<string>();
            var chain = new List<Command>();
            for (Command? c = command; c is not null; c = c.Parents.OfType<Command>().FirstOrDefault())
                chain.Insert(0, c);

            for (int i = 0; i < chain.Count; i++)
            {
                var c = chain[i];
                // The root command's display name is always the short "lpb".
                parts.Add(i == 0 ? "lpb" : c.Name);
                foreach (var arg in c.Arguments.Where(a => !a.IsHidden))
                    parts.Add(FormatArgument(arg));
            }

            if (command.Subcommands.Any(s => !s.IsHidden))
                parts.Add("[command]");
            if (command.Options.Any(o => !o.IsHidden))
                parts.Add("[options]");
            if (!command.TreatUnmatchedTokensAsErrors)
                parts.Add("[<additional arguments>...]");

            return string.Join(" ", parts);
        }

        private static string FormatArgument(Argument argument)
        {
            var arity = argument.Arity.MaximumNumberOfValues > 1 ? "..." : "";
            var optional = argument.Arity.MinimumNumberOfValues == 0;
            return optional ? $"[<{argument.Name}>{arity}]" : $"<{argument.Name}>{arity}";
        }

        private static void WriteRows(TextWriter output, IReadOnlyList<TwoColumnHelpRow> rows)
        {
            if (rows.Count == 0) return;
            int width = rows.Max(r => r.FirstColumnText.Length);

            foreach (var row in rows)
            {
                var secondLines = row.SecondColumnText.Split('\n');
                for (int i = 0; i < secondLines.Length; i++)
                {
                    if (i == 0)
                    {
                        output.Write("  ");
                        WriteColoredLabel(output, row.FirstColumnText);
                        output.Write(new string(' ', width - row.FirstColumnText.Length + 2));
                    }
                    else
                    {
                        output.Write(new string(' ', width + 4));
                    }

                    WriteDescriptionLine(output, secondLines[i].TrimEnd());
                    output.WriteLine();
                }
            }
        }

        // ─── color helpers ───

        private static void WriteHeading(TextWriter output, string heading)
        {
            WriteColored(output, heading + Environment.NewLine, CliConsole.Accent);
        }

        // Usage line: the software name is highlighted in the brand red.
        private static void WriteUsageLine(TextWriter output, string usage)
        {
            if (!CliConsole.UseColor)
            {
                output.WriteLine($"  {usage}");
                return;
            }

            // The whole usage line is a command example: render it in purple.
            WriteColoredRgb(output, "  " + usage + Environment.NewLine, CliConsole.CommandPurple);
        }

        // First column: the whole option/command label ("-d, --dir <dir>") is
        // treated as code and rendered in purple, placeholders included.
        private static void WriteColoredLabel(TextWriter output, string label)
        {
            WriteColoredRgb(output, label, CliConsole.CommandPurple);
        }

        // Second column: plain white description. No yellow highlights —
        // the plain-table design decision keeps descriptions neutral.
        private static void WriteColoredDescription(TextWriter output, string text)
        {
            output.Write(text);
        }

        // Description text. Rule: purple belongs ONLY to command examples.
        // The command itself ("lpb merge ...") is purple; everything around it
        // (indentation, leading label, trailing explanation) stays plain white.
        private static void WriteDescriptionLine(TextWriter output, string text)
        {
            if (CliConsole.UseColor)
            {
                int cmdIdx = text.IndexOf("lpb ", StringComparison.Ordinal);
                if (cmdIdx >= 0)
                {
                    output.Write(text.Substring(0, cmdIdx)); // label + indentation, plain
                    int cmdEnd = FindCommandEnd(text, cmdIdx);
                    WriteColoredRgb(output, text.Substring(cmdIdx, cmdEnd - cmdIdx), CliConsole.CommandPurple);
                    if (cmdEnd < text.Length)
                        output.Write(text.Substring(cmdEnd)); // explanation stays plain
                    return;
                }
            }

            WriteColoredDescription(output, text);
        }

        // A command example is terminated by two consecutive spaces (the
        // explanation column) or by the end of the line.
        private static int FindCommandEnd(string text, int start)
        {
            for (int i = start; i < text.Length - 1; i++)
            {
                if (text[i] == ' ' && text[i + 1] == ' ')
                    return i;
            }
            return text.Length;
        }

        private static void WriteColored(TextWriter output, string text, ConsoleColor color)
        {
            if (!CliConsole.UseColor)
            {
                output.Write(text);
                return;
            }

            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            output.Write(text);
            Console.ForegroundColor = old;
        }

        private static void WriteColoredRgb(TextWriter output, string text, (int R, int G, int B) rgb)
        {
            if (!CliConsole.UseColor)
            {
                output.Write(text);
                return;
            }

            output.Write($"\x1b[38;2;{rgb.R};{rgb.G};{rgb.B}m{text}\x1b[0m");
        }

        private void WriteGroupedHelp(HelpContext context, Dictionary<string, string> sections, GroupHeader[] groupHeaders)
        {
            // ═══ Description ═══
            if (!string.IsNullOrWhiteSpace(context.Command.Description))
            {
                // Split into lines and print manually to preserve formatting
                foreach (var line in context.Command.Description.Split('\n'))
                {
                    var text = line.TrimEnd();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        context.Output.WriteLine();
                        continue;
                    }

                    WriteDescriptionLine(context.Output, text);
                    context.Output.WriteLine();
                }
                context.Output.WriteLine();
            }

            // ═══ Usage ═══
            WriteHeading(context.Output, "Usage:");
            context.Output.WriteLine();
            WriteUsageLine(context.Output, GetUsageLine(context.Command));
            context.Output.WriteLine();

            // ═══ Grouped options ═══
            var options = context.Command.Options
                .Where(o => !o.IsHidden)
                .ToList();

            // Description column: widest option label + 2, never narrower than 32.
            // Without this, a long label (e.g. --key-timestamp <key-timestamp>)
            // breaks the column alignment.
            int descCol = options.Count > 0
                ? Math.Max(32, options.Max(o => FormatLabel(o).Length) + 2)
                : 32;

            foreach (var group in groupHeaders)
                WriteOptionGroup(context, group.Title, options, group.Key, descCol, sections);

            // Remaining options
            var remaining = options
                .Where(o => Classify(o, sections) is null)
                .ToList();
            if (remaining.Count > 0 || !options.Any(o => o.Aliases.Contains("--help")))
            {
                WriteSection(context, "═══ OTHER ═══");
                foreach (var opt in remaining)
                    WriteOption(context, opt, descCol);
                WriteHelpOption(context, descCol);
                context.Output.WriteLine();
            }
        }

        private void WriteOptionGroup(HelpContext context, string header,
            List<Option> allOptions, string groupKey, int descCol, Dictionary<string, string> sections)
        {
            var group = allOptions
                .Where(o => Classify(o, sections) == groupKey)
                .ToList();
            if (group.Count == 0) return;

            WriteSection(context, header);
            foreach (var opt in group)
                WriteOption(context, opt, descCol);
            context.Output.WriteLine();
        }

        private static string? Classify(Option option, Dictionary<string, string> sections)
        {
            foreach (var alias in option.Aliases)
            {
                if (sections.TryGetValue(alias, out var section))
                    return section;
            }
            return null;
        }

        // ── output helpers ──

        private void WriteSection(HelpContext context, string header)
        {
            // 分组表头用青色，与其它标题一致
            CliConsole.WriteLine(header, CliConsole.Accent);
            context.Output.WriteLine();
        }

        // Build the first column: "-i, --image <image>"
        private static string FormatLabel(Option option)
        {
            var aliases = string.Join(", ", option.Aliases.OrderBy(a => a.Length));
            var typePart = option.ValueType == typeof(bool) ? "" : $" <{option.Name}>";
            return $"  {aliases}{typePart}";
        }

        private void WriteOption(HelpContext context, Option option, int descCol)
        {
            // Default hint — hardcoded for readability
            string defaultHint = "";
            if (option.Aliases.Contains("--protocol")) defaultHint = " [default: motion photo]";
            else if (option.Aliases.Contains("--naming")) defaultHint = " [default: keep]";
            else if (option.Aliases.Contains("--parallel")) defaultHint = " [default: CPU cores (max 5)]";
            else if (option.Aliases.Contains("--pairing")) defaultHint = " [default: name]";
            else if (option.Aliases.Contains("--after")) defaultHint = " [default: none]";

            var label = FormatLabel(option);
            var labelWidth = Math.Max(label.Length, 2);
            var padding = labelWidth < descCol ? new string(' ', descCol - labelWidth) : "  ";

            var desc = option.Description ?? "";
            var lines = desc.Split('\n');
            var firstLine = lines[0].Trim();

            // Line 1: plain label + description + default
            WriteColoredLabel(context.Output, label);
            context.Output.Write(padding);
            WriteDescriptionLine(context.Output, firstLine);
            if (!string.IsNullOrEmpty(defaultHint))
                context.Output.Write(defaultHint);
            context.Output.WriteLine();

            // Line 2+: continuation indented under description
            var contIndent = new string(' ', descCol + 2); // align under description text
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    context.Output.Write(contIndent);
                    WriteDescriptionLine(context.Output, line);
                    context.Output.WriteLine();
                }
            }
        }

        // The global help option is not part of merge's own Options in beta4,
        // so it is rendered manually at the end of the OTHER section.
        private static void WriteHelpOption(HelpContext context, int descCol)
        {
            var label = "  -?, -h, --help";
            var padding = new string(' ', descCol - label.Length);
            WriteColoredLabel(context.Output, label);
            context.Output.Write(padding);
            WriteColoredDescription(context.Output, "Show help and usage information");
            context.Output.WriteLine();
        }
    }
}
