using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.Linq;
using System.Text.Json;

namespace LivePhotoBox.Cli.Commands
{
    internal static class ProtocolsCommand
    {
        // Devices supported per merge protocol, aligned with ProtocolFormatMatrix.Matrix index.
        // CLI-only display data (the GUI picks protocols via a combo box and does not need this table).
        private static readonly string[] MergeDevices =
        [
            "Windows / Android (universal)",          // 0 fusion
            "Windows / Xiaomi (legacy MIUI) / Pixel", // 1 micro video
            "Windows / Xiaomi / Pixel",               // 2 motion photo
            "Windows / Xiaomi / OPPO",                // 3 oppo
            "Windows / vivo (≥ X300)",                // 4 vivo
            "Windows / Samsung",                      // 5 samsung
            "HUAWEI / Honor",                         // 6 huawei
        ];

        // true = supported, false = in testing
        private static readonly bool[] MergeSupported =
            [false, true, true, true, false, false, true];

        // Split protocols — the split command writes no pairing metadata this iteration (protocol is a placeholder).
        private static readonly (string Name, string Devices, bool Supported)[] SplitProtocols =
        [
            ("None (split only)",  "Any device", true),
            ("Apple Live Photo",   "iPhone / iPad", true),
            ("vivo Live Photo",    "vivo (≤ X200)", false),
        ];

        public static Command Create()
        {
            var jsonOpt = new Option<bool>("--json", "Output in JSON format");

            var cmd = new Command("protocols", "List supported protocols and format combinations")
            {
                jsonOpt
            };

            cmd.SetHandler(json =>
            {
                if (json)
                    PrintJson();
                else
                    PrintTable();
            }, jsonOpt);

            return cmd;
        }

        private static void PrintTable()
        {
            Console.WriteLine();
            CliConsole.WriteLine("Merge — protocol × format compatibility", CliConsole.Accent);
            Console.WriteLine();

            // 格式列使用带空格的可读名（如 JPEG + MP4），列宽 = 名称长度 + 2 空格间隔
            string[] fmt = ProtocolFormatMatrix.FormatNames;
            int[] colW = fmt.Select(f => f.Length + 2).ToArray();

            Console.Write($"{"Protocol".PadRight(22)}");
            for (int f = 0; f < fmt.Length; f++)
                Console.Write(fmt[f].PadRight(colW[f]));
            Console.WriteLine();

            Console.Write($"{new string('─', 22)} ");
            for (int f = 0; f < fmt.Length; f++)
                Console.Write(new string('─', colW[f] - 2) + "  ");
            Console.WriteLine();

            for (int p = 1; p < ProtocolFormatMatrix.Matrix.Length; p++)
            {
                string display = ProtocolNameResolver.ProtocolDisplayNames[p];
                Console.Write(display.PadRight(22));
                Console.Write(" ");
                for (int f = 0; f < fmt.Length; f++)
                {
                    WriteMark(ProtocolFormatMatrix.Matrix[p][f]);
                    Console.Write(new string(' ', colW[f] - 2)); // ✅/✖️ 在控制台约占 2 个单元宽度
                }
                Console.WriteLine();
            }

            Console.WriteLine();
            WriteLegend();
            Console.WriteLine();
            WriteIndexLine("Protocol indices: ", "micro video=1  motion photo=2  oppo=3  vivo=4  samsung=5  huawei=6");
            WriteIndexLine("Format indices:   ", "jpg+mp4=0  jpg+mov=1  heic+mp4=2  heic+mov=3  heic+mp4-h265=4");
            Console.WriteLine();

            PrintDeviceTable("Merge — devices & availability",
                ProtocolNameResolver.ProtocolDisplayNames.Skip(1).ToArray(),
                MergeDevices.Skip(1).ToArray(),
                MergeSupported.Skip(1).ToArray());
            PrintDeviceTable("Split — devices & availability",
                SplitProtocols.Select(p => p.Name).ToArray(),
                SplitProtocols.Select(p => p.Devices).ToArray(),
                SplitProtocols.Select(p => p.Supported).ToArray());

            PrintSplitFormatMatrix();
            WriteIndexLine("Split protocol indices: ", "none=0  apple=1  vivo=2");
            WriteIndexLine("Split format indices:   ", "keep=0  jpg+mov=1  heic+mov=2  jpg+mp4=3");

            // Repair — fixes metadata; no protocol choice involved.
            Console.WriteLine();
            CliConsole.WriteLine("Repair — metadata fixes (no protocol needed)", CliConsole.Accent);
            Console.WriteLine();
            Console.WriteLine("Fixes rotation, embedded thumbnails, HEIC orientation, and video rotation.");
            Console.WriteLine("Apple Live Photos only (identified by ContentIdentifier UUID).");
        }

        // Prints a "protocol × devices × availability" table (merge or split).
        private static void PrintDeviceTable(string title, string[] names, string[] devices, bool[] supported)
        {
            int nameW = Math.Max("Protocol".Length, names.Max(n => n.Length));
            int devW = Math.Max("Devices".Length, devices.Max(d => d.Length));

            Console.WriteLine();
            CliConsole.WriteLine($"{title}", CliConsole.Accent);
            Console.WriteLine();
            Console.Write("Protocol".PadRight(nameW));
            Console.Write("   ");
            Console.Write("Devices".PadRight(devW));
            Console.Write("   Status");
            Console.WriteLine();
            Console.WriteLine($"{new string('─', nameW)}   {new string('─', devW)}   ──────────");

            for (int i = 0; i < names.Length; i++)
            {
                Console.Write(names[i].PadRight(nameW));
                Console.Write("   ");
                Console.Write(devices[i].PadRight(devW));
                Console.Write("   ");
                WriteAvailability(supported[i]);
                Console.WriteLine();
            }
        }

        // Prints the split "protocol × format" compatibility matrix (none/apple/vivo × keep/jpg+mov/heic+mov/jpg+mp4).
        private static void PrintSplitFormatMatrix()
        {
            Console.WriteLine();
            CliConsole.WriteLine("Split — protocol × format compatibility", CliConsole.Accent);
            Console.WriteLine();

            // split 格式列也统一为带空格的可读名（jpg+mov → JPG + MOV）
            string[] fmtNames = SplitCommand.SplitFormatNames
                .Select(f => f == "keep" ? "keep" : f.ToUpperInvariant().Replace("+", " + "))
                .ToArray();
            int nameW = Math.Max("Protocol".Length, SplitProtocols.Max(p => p.Name.Length));
            int fmtW = Math.Max(8, fmtNames.Max(f => f.Length) + 1);

            Console.Write("Protocol".PadRight(nameW));
            Console.Write(" ");
            for (int f = 0; f < fmtNames.Length; f++)
            {
                Console.Write(fmtNames[f].PadRight(fmtW));
                Console.Write("   ");
            }
            Console.WriteLine();

            Console.Write(new string('─', nameW));
            Console.Write(" ");
            for (int f = 0; f < fmtNames.Length; f++)
            {
                Console.Write(new string('─', fmtW));
                Console.Write("   ");
            }
            Console.WriteLine();

            for (int s = 0; s < SplitProtocols.Length; s++)
            {
                Console.Write(SplitProtocols[s].Name.PadRight(nameW));
                Console.Write(" ");
                for (int f = 0; f < fmtNames.Length; f++)
                {
                    WriteMark(SplitCommand.SplitFormatMatrix[s][f]);
                    Console.Write(new string(' ', fmtW - 2));
                    Console.Write("   ");
                }
                Console.WriteLine();
            }
        }

        private static void WriteAvailability(bool supported)
        {
            if (CliConsole.UseColor)
            {
                if (supported)
                {
                    CliConsole.Write("✅", CliConsole.Success);
                    Console.Write(" Supported");
                }
                else
                {
                    CliConsole.Write("🟡", CliConsole.Notice);
                    Console.Write(" In testing");
                }
            }
            else
            {
                Console.Write(supported ? "✅ Supported" : "🟡 In testing");
            }
        }

        // "fusion=0  micro video=1 ..." — name plain white, "=index" in yellow.
        private static void WriteIndexLine(string prefix, string rest)
        {
            Console.Write(prefix);
            if (!CliConsole.UseColor)
            {
                Console.WriteLine(rest);
                return;
            }

            // 条目以两个空格分隔；条目内部的名字可含单个空格（如 "micro video=1"）。
            foreach (var entry in rest.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = entry.IndexOf('=');
                if (eq > 0)
                {
                    Console.Write(entry.Substring(0, eq));
                    CliConsole.Write(entry.Substring(eq), CliConsole.Highlight);
                }
                else
                {
                    Console.Write(entry);
                }
                Console.Write("  ");
            }
            Console.WriteLine();
        }

        private static void WriteMark(bool available)
        {
            if (CliConsole.UseColor)
            {
                CliConsole.Write($"  {(available ? "✅" : "✖️")}",
                    available ? CliConsole.Success : CliConsole.Error);
            }
            else
            {
                Console.Write(available ? "  ✅" : "  ✖️");
            }
        }

        private static void WriteLegend()
        {
            if (CliConsole.UseColor)
            {
                CliConsole.Write("✅", CliConsole.Success);
                Console.Write(" = supported   ");
                CliConsole.Write("✖️", CliConsole.Error);
                Console.WriteLine(" = not supported");
            }
            else
            {
                Console.WriteLine("✅ = supported   ✖️ = not supported");
            }
        }

        private static void PrintJson()
        {
            var protocols = new object[ProtocolFormatMatrix.Matrix.Length - 1];
            for (int p = 1; p < ProtocolFormatMatrix.Matrix.Length; p++)
            {
                int fmtCount = ProtocolFormatMatrix.FormatNames.Length;
                var formats = new string[fmtCount];
                for (int f = 0; f < fmtCount; f++)
                    formats[f] = ProtocolFormatMatrix.Matrix[p][f] ? ProtocolFormatMatrix.FormatNames[f] : null!;

                protocols[p - 1] = new
                {
                    index = p,
                    name = ProtocolNameResolver.ProtocolNames[p],
                    displayName = ProtocolNameResolver.ProtocolDisplayNames[p],
                    devices = MergeDevices[p],
                    status = MergeSupported[p] ? "Supported" : "In testing",
                    formats = Array.FindAll(formats, f => f != null)
                };
            }

            var split = new object[SplitProtocols.Length];
            for (int s = 0; s < SplitProtocols.Length; s++)
            {
                int fmtCount = SplitCommand.SplitFormatNames.Length;
                var formats = new string[fmtCount];
                for (int f = 0; f < fmtCount; f++)
                    formats[f] = SplitCommand.SplitFormatMatrix[s][f] ? SplitCommand.SplitFormatNames[f] : null!;

                split[s] = new
                {
                    index = s,
                    name = SplitProtocols[s].Name,
                    devices = SplitProtocols[s].Devices,
                    status = SplitProtocols[s].Supported ? "Supported" : "In testing",
                    formats = Array.FindAll(formats, f => f != null)
                };
            }

            var json = JsonSerializer.Serialize(new { protocols, split }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
    }
}
