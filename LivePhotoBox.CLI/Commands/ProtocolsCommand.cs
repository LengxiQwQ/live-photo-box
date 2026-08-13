using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.Text.Json;

namespace LivePhotoBox.Cli.Commands
{
    internal static class ProtocolsCommand
    {
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
            CliConsole.WriteLine("  Merge — protocol × format compatibility", CliConsole.Notice);
            Console.WriteLine();
            CliConsole.WriteLine($"  {"Protocol".PadRight(22)}JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+MP4(H.265)", CliConsole.Accent);
            Console.WriteLine($"  {new string('─', 22)} ────────   ────────   ────────   ────────   ──────────────");

            for (int p = 0; p < ProtocolFormatMatrix.Matrix.Length; p++)
            {
                string display = p == 0 ? "Fusion (testing)" : ProtocolNameResolver.ProtocolDisplayNames[p];
                string name = display.PadRight(22);
                Console.Write("  ");
                CliConsole.Write(name, CliConsole.Accent);
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
        }

        // "fusion=0  micro video=1 ..." — index numbers (values) in yellow.
        private static void WriteIndexLine(string prefix, string rest)
        {
            Console.Write(prefix);
            if (!CliConsole.UseColor)
            {
                Console.WriteLine(rest);
                return;
            }

            foreach (var seg in rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = seg.IndexOf('=');
                if (eq > 0)
                {
                    Console.Write(seg.Substring(0, eq + 1));
                    CliConsole.Write(seg.Substring(eq + 1), CliConsole.Highlight);
                }
                else
                {
                    Console.Write(seg);
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
                    formats = Array.FindAll(formats, f => f != null)
                };
            }

            var json = JsonSerializer.Serialize(new { protocols }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
    }
}
