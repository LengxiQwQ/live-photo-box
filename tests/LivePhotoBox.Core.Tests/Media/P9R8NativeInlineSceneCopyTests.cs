using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8NativeInlineSceneCopyTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("non-copy")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("overflow")]
    [InlineData("scene-slices")]
    [InlineData("nested-reference")]
    [InlineData("timestamp-order")]
    [InlineData("escaped-string")]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("implicit")]
    [InlineData("oversized")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task ActualCopyRequiresClosedInlineSceneGrammarAndClearsFailureProof(string defect)
    {
        string original=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/荣耀.jpg"));
        string originalHash=Hash(File.ReadAllBytes(original));using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(original,null,workspace);
        byte[] b=File.ReadAllBytes(Assert.IsType<MediaArtifact>(bundle.MotionVideo).Path);
        var movie=One(Children(b,0,b.Length),"moov");var scene=One(Children(b,movie.Body,movie.End),"gnre");
        foreach(var mdat in Children(b,0,b.Length).Where(x=>x.Kind=="mdat"))Assert.True(mdat.End<=movie.Body-8);
        string text=Encoding.UTF8.GetString(b,scene.Body,scene.End-scene.Body),changed=text;
        (string Before,string After) edit=defect switch
        {
            "valid" or "non-copy" or "prefix" or "suffix" or "implicit" or "oversized"=>("",""),
            "duplicate"=>("\"effect\":1","\"effect\":1,\"effect\":1"),
            "unknown"=>("\"effect\":1","\"offset\":1"),
            "missing"=>("\"effect\":1,",""),
            "overflow"=>("\"score\":22.755201","\"score\":1e999"),
            "scene-slices"=>("\"sceneSlices\":[]","\"sceneSlices\":[{}]"),
            "nested-reference"=>("\"sceneSlices\":[]","\"sceneSlices\":[],\"offset\":0"),
            "timestamp-order"=>("\"endTime\":1784956304054","\"endTime\":0"),
            "escaped-string"=>("\"scene\":\"122\"","\"scene\":\"\\u0031\""),
            _=>throw new ArgumentOutOfRangeException(nameof(defect))
        };
        if(edit.Before.Length>0)
        {
            Assert.Contains(edit.Before,text);changed=text.Replace(edit.Before,edit.After,StringComparison.Ordinal);Assert.NotEqual(text,changed);
        }
        else if(defect=="oversized")changed=text.Insert(6,new string(' ',16384));
        if(changed!=text)
        {
            byte[] payload=Encoding.UTF8.GetBytes(changed);int delta=payload.Length-(scene.End-scene.Body);
            b=b[..scene.Body].Concat(payload).Concat(b[scene.End..]).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(scene.Body-8),checked((uint)(payload.Length+8)));
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(movie.Body-8),checked((uint)(movie.End-movie.Body+8+delta)));
        }
        if(defect=="prefix")b[scene.Body]=1;
        if(defect=="suffix")b[scene.End-1]=1;
        if(defect=="implicit")b.AsSpan(scene.Body-8,4).Clear();
        using var folder=new P9TargetTestInputs.Folder(nameof(ActualCopyRequiresClosedInlineSceneGrammarAndClearsFailureProof)+defect);
        string input=Path.Combine(folder.Path,"source.mp4"),output=Path.Combine(folder.Path,"target.mov");File.WriteAllBytes(input,b);
        using var context=NativeContext.Create();var request=new NativeVideoPreservationRequestV1{StructSize=16,ApiVersion=1,Policy=1};
        var diagnostic=new NativeVideoBackendDiagnostics{StructSize=208};var proof=new NativeVideoPreservationResultV2{StructSize=192,ApiVersion=2,ProofKind=1,ProofFlags=1};
        var result=NativeMethods.TranscodeVideoV4(context.Handle,input,output,targetContainer:2,targetCodec:defect=="non-copy"?2:1,crf:23,in request,ref diagnostic,ref proof);
        if(defect=="valid")
        {
            Assert.Equal(NativeResult.Ok,result);Assert.Equal(1U,proof.ProofKind);Assert.Equal(1U,proof.ProofFlags);
            Assert.Equal((ulong)b.Length,proof.SourceLength);Assert.Equal((ulong)new FileInfo(output).Length,proof.OutputLength);
            Assert.Equal(Hash(b),ProofHash(proof,true));Assert.Equal(Hash(File.ReadAllBytes(output)),ProofHash(proof,false));
            var target=File.ReadAllBytes(output);var tm=One(Children(target,0,target.Length),"moov");var ts=One(Children(target,tm.Body,tm.End),"gnre");
            Assert.Equal(b[(scene.Body-8)..scene.End],target[(ts.Body-8)..ts.End]);Assert.Equal(2,Directory.GetFiles(folder.Path).Length);
            if(Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_SCENE_PROOF_SNAPSHOT") is { Length: > 0 } snapshot)
            {
                Assert.False(Directory.Exists(snapshot));Directory.CreateDirectory(snapshot);
                File.Copy(input,Path.Combine(snapshot,"source.mp4"));File.Copy(output,Path.Combine(snapshot,"copy.mov"));
                var native=Assert.Single(System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>(),m=>m.ModuleName=="LivePhotoBox.Native.dll");
                File.WriteAllText(Path.Combine(snapshot,"proof.json"),System.Text.Json.JsonSerializer.Serialize(new
                {
                    Original=original,OriginalSha256=originalHash,SourceSha256=ProofHash(proof,true),OutputSha256=ProofHash(proof,false),
                    proof.SourceLength,proof.OutputLength,proof.ProofKind,proof.ProofFlags,MetadataStatus=proof.Metadata.Status,
                    NativePath=native.FileName,NativeSha256=Hash(File.ReadAllBytes(native.FileName))
                },new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            }
        }
        else
        {
            Assert.NotEqual(NativeResult.Ok,result);Assert.Equal(0U,proof.ProofKind);Assert.Equal(0U,proof.ProofFlags);Assert.Equal(0U,proof.Metadata.Status);
            Assert.False(File.Exists(output));Assert.Equal(input,Assert.Single(Directory.GetFiles(folder.Path)));
        }
        Assert.Equal(b,File.ReadAllBytes(input));Assert.Equal(originalHash,Hash(File.ReadAllBytes(original)));
    }
    private static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    private static unsafe string ProofHash(NativeVideoPreservationResultV2 proof,bool source){byte* b=source?proof.SourceFileSha256:proof.OutputFileSha256;return Convert.ToHexString(new ReadOnlySpan<byte>(b,32));}
    private sealed record Box(int Body,int End,string Kind);
    private static Box One(IReadOnlyList<Box> rows,string kind)=>Assert.Single(rows,x=>x.Kind==kind);
    private static List<Box> Children(byte[] b,int at,int end)
    {
        var rows=new List<Box>();while(at<end){int n=checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at))),header=8;if(n==1){n=checked((int)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at+8)));header=16;}Assert.InRange(n,header,end-at);rows.Add(new(at+header,at+n,Encoding.ASCII.GetString(b,at+4,4)));at+=n;}Assert.Equal(end,at);return rows;
    }
}
