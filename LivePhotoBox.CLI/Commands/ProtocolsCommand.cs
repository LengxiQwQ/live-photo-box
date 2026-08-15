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
            ("Apple Live Photo",   "iPhone / iPad", false),
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
            CliConsole.WriteLine("  Merge — protocol × format compatibility", CliConsole.Accent);
            Console.WriteLine();
            Console.WriteLine($"  {"Protocol".PadRight(22)}JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+MP4(H.265)");
            Console.WriteLine($"  {new string('─', 22)} ────────   ────────   ────────   ────────   ──────────────");

            for (int p = 0; p < ProtocolFormatMatrix.Matrix.Length; p++)
            {
                string display = p == 0 ? "Fusion (testing)" : ProtocolNameResolver.ProtocolDisplayNames[p];
                string name = display.PadRight(22);
                Console.Write("  ");
                Console.Write(name);
                Console.Write(" ");
                WriteMark(ProtocolFormatMatrix.Matrix[p][0]);
                Console.Write("        ");
                WriteMark(ProtocolFormatMatrix.Matrix[p][1]);
                Console.Write("        ");
                WriteMark(ProtocolFormatMatrix.Matrix[p][2]);
                Console.Write("        ");
                WriteMark(ProtocolFormatMatrix.Matrix[p][3]);
                Console.Write("        ");
                WriteMark(ProtocolFormatMatrix.Matrix[p][4]);
                Console.WriteLine();
            }

            Console.WriteLine();
            WriteLegend();
            Console.WriteLine();
            WriteIndexLine("  Protocol indices: ", "fusion=0  micro video=1  motion photo=2  oppo=3  vivo=4  samsung=5  huawei=6");
            WriteIndexLine("  Format indices:   ", "jpg+mp4=0  jpg+mov=1  heic+mp4=2  heic+mov=3  heic+mp4-h265=4");
            Console.WriteLine();

            PrintDeviceTable("Merge — devices & availability",
                ProtocolNameResolver.ProtocolDisplayNames, MergeDevices, MergeSupported);
            PrintDeviceTable("Split — devices & availability",
                SplitProtocols.Select(p => p.Name).ToArray(),
                SplitProtocols.Select(p => p.Devices).ToArray(),
                SplitProtocols.Select(p => p.Supported).ToArray());

            // Repair — fixes metadata; no protocol choice involved.
            Console.WriteLine();
            CliConsole.WriteLine("  Repair — metadata fixes (no protocol needed)", CliConsole.Accent);
            Console.WriteLine();
            Console.WriteLine("    Fixes rotation, embedded thumbnails, HEIC orientation, and video rotation.");
            Console.WriteLine("    Apple Live Photos only (identified by ContentIdentifier UUID).");
        }

        // Prints a "protocol × devices × availability" table (merge or split).
        private static void PrintDeviceTable(string title, string[] names, string[] devices, bool[] supported)
        {
            int nameW = Math.Max("Protocol".Length, names.Max(n => n.Length));
            int devW = Math.Max("Devices".Length, devices.Max(d => d.Length));

            Console.WriteLine();
            CliConsole.WriteLine($"  {title}", CliConsole.Accent);
            Console.WriteLine();
            Console.Write("  ");
            Console.Write("Protocol".PadRight(nameW));
            Console.Write("   ");
            Console.Write("Devices".PadRight(devW));
            Console.Write("   Status");
            Console.WriteLine();
            Console.WriteLine($"  {new string('─', nameW)}   {new string('─', devW)}   ──────────");

            for (int i = 0; i < names.Length; i++)
            {
                Console.Write("  ");
                Console.Write(names[i].PadRight(nameW));
                Console.Write("   ");
                Console.Write(devices[i].PadRight(devW));
                Console.Write("   ");
                WriteAvailability(supported[i]);
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
                Console.Write("  ");
                CliConsole.Write("✅", CliConsole.Success);
                Console.Write(" = supported   ");
                CliConsole.Write("✖️", CliConsole.Error);
                Console.WriteLine(" = not supported");
            }
            else
            {
                Console.WriteLine("  ✅ = supported   ✖️ = not supported");
            }
        }

        private static void PrintJson()
        {
            var protocols = new object[ProtocolFormatMatrix.Matrix.Length];
            for (int p = 0; p < ProtocolFormatMatrix.Matrix.Length; p++)
            {
                int fmtCount = ProtocolFormatMatrix.FormatNames.Length;
                var formats = new string[fmtCount];
                for (int f = 0; f < fmtCount; f++)
                    formats[f] = ProtocolFormatMatrix.Matrix[p][f] ? ProtocolFormatMatrix.FormatNames[f] : null!;

                protocols[p] = new
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
                split[s] = new
                {
                    name = SplitProtocols[s].Name,
                    devices = SplitProtocols[s].Devices,
                    status = SplitProtocols[s].Supported ? "Supported" : "In testing"
                };
            }

            var json = JsonSerializer.Serialize(new { protocols, split }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
    }
}
