using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class AppleMakerNoteReaderTests
{
    [Fact]
    public void ReadContentIdentifier_FromSingleMakerNote_ReturnsCid()
    {
        string temp = CreateTempFile();
        try
        {
            byte[] mn = AppleMakerNoteWriter.BuildMakerNote("11111111-2222-3333-4444-555566667777");
            File.WriteAllBytes(temp, mn);

            bool ok = AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out string? cid, out string? error);

            Assert.True(ok, $"expected read success, error={error}");
            Assert.Equal("11111111-2222-3333-4444-555566667777", cid);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [Fact]
    public void ReadContentIdentifier_SkipsStrippedMakerNote_FindsLaterCid()
    {
        string temp = CreateTempFile();
        try
        {
            // 模拟 JPEG 注入路径：旧 Apple MakerNote（CID 已被剥离）在前，
            // 新注入的含 CID MakerNote 追加在后。读取器必须跳过空壳 MN。
            byte[] oldMn = AppleMakerNoteWriter.BuildMakerNote("99999999-8888-7777-6666-555544443333");
            File.WriteAllBytes(temp, oldMn);
            Assert.True(AppleMakerNoteWriter.TryStripAppleLivePhotoEntries(temp, out string? stripError),
                $"strip failed: {stripError}");

            byte[] stripped = File.ReadAllBytes(temp);
            byte[] newMn = AppleMakerNoteWriter.BuildMakerNote("AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000");
            File.WriteAllBytes(temp, Concat(stripped, newMn));

            bool ok = AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out string? cid, out string? error);

            Assert.True(ok, $"expected read success, error={error}");
            Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000", cid);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string CreateTempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lpb_mn_tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}.bin");
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 测试清理失败不掩盖断言结果
        }
    }
}
