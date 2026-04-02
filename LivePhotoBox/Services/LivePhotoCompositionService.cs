using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class LivePhotoCompositionService
    {
        public static string CreateOutputFileName(string baseName, int selectedModeIndex)
        {
            return selectedModeIndex == 0 ? $"MVIMG_{baseName}.jpg" : $"{baseName}.MP.jpg";
        }

        public static async Task WriteLivePhotoAsync(string sourceImg, string sourceVid, string targetPath, int selectedModeIndex, CancellationToken token)
        {
            long videoSize = new FileInfo(sourceVid).Length;
            string xmpContent = CreateXmpContent(selectedModeIndex, videoSize);
            await WriteNativeAsync(sourceImg, sourceVid, targetPath, xmpContent, token);
        }

        private static string CreateXmpContent(int selectedModeIndex, long videoSize)
        {
            if (selectedModeIndex == 0)
            {
                return $@"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description rdf:about="""" xmlns:GCamera=""http://ns.google.com/photos/1.0/camera/""
      GCamera:MicroVideo=""1"" 
      GCamera:MicroVideoVersion=""1"" 
      GCamera:MicroVideoOffset=""{videoSize}"" 
      GCamera:MicroVideoPresentationTimestampUs=""0""/>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";
            }

            return $@"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/""><rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
<rdf:Description rdf:about="""" xmlns:GCamera=""http://ns.google.com/photos/1.0/camera/"" xmlns:Container=""http://ns.google.com/photos/1.0/container/"" xmlns:Item=""http://ns.google.com/photos/1.0/container/item/""
GCamera:MotionPhoto=""1"" GCamera:MotionPhotoVersion=""1"" GCamera:MotionPhotoPresentationTimestampUs=""0"">
<Container:Directory><rdf:Seq><rdf:li rdf:parseType=""Resource""><Container:Item Item:Mime=""image/jpeg"" Item:Semantic=""Primary"" Item:Length=""0"" Item:Padding=""0""/></rdf:li>
<rdf:li rdf:parseType=""Resource""><Container:Item Item:Mime=""video/mp4"" Item:Semantic=""MotionPhoto"" Item:Length=""{videoSize}"" Item:Padding=""0""/></rdf:li>
</rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=""w""?>";
        }

        private static async Task WriteNativeAsync(string sourceImg, string sourceVid, string targetPath, string xmpXml, CancellationToken token)
        {
            byte[] xmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
            byte[] xmpXmlBytes = Encoding.UTF8.GetBytes(xmpXml);

            int segmentLength = 2 + xmpHeader.Length + xmpXmlBytes.Length;
            if (segmentLength > ushort.MaxValue)
            {
                throw new InvalidOperationException("XMP 元数据过大，无法写入 JPEG APP1 段");
            }

            using var imgFs = new FileStream(sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (imgFs.Length < 2 || imgFs.ReadByte() != 0xFF || imgFs.ReadByte() != 0xD8)
            {
                throw new InvalidDataException("源图像不是有效的 JPEG 文件");
            }

            using var targetFs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            targetFs.WriteByte(0xFF);
            targetFs.WriteByte(0xD8);
            targetFs.WriteByte(0xFF);
            targetFs.WriteByte(0xE1);
            targetFs.WriteByte((byte)(segmentLength >> 8));
            targetFs.WriteByte((byte)(segmentLength & 0xFF));
            await targetFs.WriteAsync(xmpHeader, 0, xmpHeader.Length, token);
            await targetFs.WriteAsync(xmpXmlBytes, 0, xmpXmlBytes.Length, token);

            await imgFs.CopyToAsync(targetFs, token);

            using var vidFs = new FileStream(sourceVid, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            await vidFs.CopyToAsync(targetFs, token);
        }
    }
}
