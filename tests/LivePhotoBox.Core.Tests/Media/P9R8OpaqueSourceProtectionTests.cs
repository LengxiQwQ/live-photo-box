using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8OpaqueSourceProtectionTests
{
    [Theory]
    [InlineData("一加.jpg",846375)]
    [InlineData("一加-改了封面照片.jpg",801)]
    [InlineData("oppo.jpg",402)]
    [Trait("Category","RealSamples")]
    public async Task OldExtractionCannotDiscardUnrepresentedOrdinaryTrailer(string name,long tailLength)
    {
        string source=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));
        Assert.True(File.Exists(source),"Required original cached source missing");
        string hash=Hash(source);
        using var inspected=await new SourceInspector().InspectWithPlanAsync(source);
        Assert.Equal(SourceProtocol.OppoLivePhoto,inspected.Facts.Protocol);
        Assert.Equal(tailLength,inspected.Facts.ProtocolTailLength);
        using var folder=new P9TargetTestInputs.Folder(nameof(OldExtractionCannotDiscardUnrepresentedOrdinaryTrailer)+tailLength);
        string image=Path.Combine(folder.Path,"image.jpg"),video=Path.Combine(folder.Path,"motion.mp4"),gain=Path.Combine(folder.Path,"gain.jpg");
        var auxiliary=inspected.Facts.AuxiliaryItems.Select((item,index)=>(item,index))
            .Where(row=>row.item.IsPresent&&row.item.Representation==AuxiliaryRepresentation.Materialized&&
                !(inspected.Facts.GainMap is {IsPresent:true} gm&&gm.AuxiliaryIndex==(uint)row.index))
            .Select(row=>new NativeMediaService.NativeAuxiliaryOutputBinding((uint)row.index,Path.Combine(folder.Path,$"aux-{row.index}.jpg"))).ToArray();
        Exception? error;
        using(var attempt=inspected.ExtractionPlan.BeginExtractionAttempt(CancellationToken.None))
            error=await Record.ExceptionAsync(()=>NativeMediaService.ExtractMediaAsync(attempt,source,null,image,video,gain,auxiliary,null,null));
        Assert.NotNull(error);Assert.Contains("no authorized preservation handoff",error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));Assert.Equal(hash,Hash(source));
        var replay=await Record.ExceptionAsync(()=>NativeMediaService.ExtractMediaAsync(inspected.ExtractionPlan,source,null,image,video,gain));
        Assert.NotNull(replay);Assert.Empty(Directory.GetFileSystemEntries(folder.Path));Assert.Equal(hash,Hash(source));
    }
    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
}
