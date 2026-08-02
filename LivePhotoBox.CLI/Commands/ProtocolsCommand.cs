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
            Console.WriteLine("  Protocol          JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+H265");
            Console.WriteLine("  ─────────         ────────   ────────   ────────   ────────   ────────");

            for (int p = 0; p < ProtocolFormatMatrix.Matrix.Length; p++)
            {
                string name = ProtocolNameResolver.ProtocolNames[p].PadRight(18);
                string f0 = Mark(ProtocolFormatMatrix.Matrix[p][0]);
                string f1 = Mark(ProtocolFormatMatrix.Matrix[p][1]);
                string f2 = Mark(ProtocolFormatMatrix.Matrix[p][2]);
                string f3 = Mark(ProtocolFormatMatrix.Matrix[p][3]);
                string f4 = Mark(ProtocolFormatMatrix.Matrix[p][4]);
                Console.WriteLine($"  {name} {f0}        {f1}        {f2}        {f3}        {f4}");
            }

            Console.WriteLine();
            Console.WriteLine("  ✅ = supported   ── = not supported");
            Console.WriteLine();
            Console.WriteLine("  Protocol indices: fusion=0  v1=1  v2=2  oppo=3  vivo=4  samsung=5  huawei=6");
            Console.WriteLine("  Format indices:   jpg+mp4=0  jpg+mov=1  heic+mp4=2  heic+mov=3  heic+mp4-h265=4");
            Console.WriteLine();
        }

        private static string Mark(bool available) => available ? "  ✅" : "  ──";

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
                    displayName = ProtocolNameResolver.GetProtocolDisplayName(p),
                    formats = Array.FindAll(formats, f => f != null)
                };
            }

            var json = JsonSerializer.Serialize(new { protocols }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
    }
}
