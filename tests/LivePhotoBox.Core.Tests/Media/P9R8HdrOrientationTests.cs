using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8HdrOrientationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public P9R8HdrOrientationTests(Xunit.Abstractions.ITestOutputHelper output)=>_output=output;
    [Theory]
    [InlineData(6)]
    [InlineData(2)]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task StoredPrimaryAndGainMapKeepExactExifAndCommonDisplayOrientation(int orientation)
    {
        byte[] original=File.ReadAllBytes(Source());string originalHash=Hash(original);
        byte[] input=orientation==6?original:Fixture(original,"mirror");
        using var folder=new P9TargetTestInputs.Folder(nameof(StoredPrimaryAndGainMapKeepExactExifAndCommonDisplayOrientation)+orientation);
        string path=Path.Combine(folder.Path,"input.heic");File.WriteAllBytes(path,input);
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(path,null,workspace);
        var binding=Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        byte[] neutral=File.ReadAllBytes(bundle.PrimaryImage.Path);byte[] beforeExif=Exif(neutral);
        using(var transaction=await NativeMediaService.StageHeicGainMapToJpegUltraHdrAsync(bundle.PrimaryImage.Path,
            Path.Combine(folder.Path,"not-published.jpg"),binding,bundle.PrimaryImage.ByteLength,92,HdrOutputPolicy.PreserveHdr))
        {
            var inspected=transaction.Inspect();var proof=transaction.GetOrdinaryXmpProof();
            Assert.NotNull(proof);Assert.Equal(3U,proof.Value.Kind);
            Assert.Equal(Hash(neutral),proof.Value.SourceSha256);
            byte[] output=ReadShared(transaction.StagingPath);Assert.Equal(Hash(output),proof.Value.OutputSha256);
            Assert.Equal(beforeExif,JpegExif(output));
            Assert.Equal(orientation,inspected.Preservation.Orientation);
            // The general source-facts reader does not populate JPEG dimensions.
            // Verify both the actual SOF bytes and the conversion's encoded-size result.
            Assert.Equal((4000,3000),JpegDimensions(output));
            Assert.Equal(4000U,transaction.Result.PrimaryWidth);Assert.Equal(3000U,transaction.Result.PrimaryHeight);
            Assert.Equal(1000U,transaction.Result.GainMapWidth);Assert.Equal(750U,transaction.Result.GainMapHeight);
            Assert.Equal((1000,750),JpegDimensions(output[checked((int)transaction.Result.GainMapRange.Offset)..]));
            using(var foreign=NativeContext.Create())
            {
                var denied=new NativeHdrMetadataProofV2{StructSize=80,ApiVersion=2,ProofKind=3,ProofFlags=1};
                Assert.NotEqual(NativeResult.Ok,NativeMethods.GetHdrMetadataProofV2(foreign.Handle,transaction.Result.TransactionToken,ref denied));
                Assert.Equal(0U,denied.ProofKind);Assert.Equal(0U,denied.ProofFlags);
            }
            Assert.False(File.Exists(Path.Combine(folder.Path,"not-published.jpg")));
            string? root=Environment.GetEnvironmentVariable("LPB_P9R8_IMAGE_STAGE_SNAPSHOT_ROOT");
            if(root is not null)
            {
                string destination=Path.Combine(root,"orientation-"+orientation);
                Assert.False(Directory.Exists(destination));Directory.CreateDirectory(destination);
                File.WriteAllBytes(Path.Combine(destination,"source.heic"),input);
                File.WriteAllBytes(Path.Combine(destination,"neutral.heic"),neutral);
                File.WriteAllBytes(Path.Combine(destination,"diagnostic-stage.jpg"),output);
                File.WriteAllText(Path.Combine(destination,"identity.json"),JsonSerializer.Serialize(new
                {
                    category=orientation==6?"RealSample working copy":"Derived real-container mirror fixture",
                    originalHash,sourceHash=Hash(input),neutralHash=Hash(neutral),stageHash=Hash(output),
                    orientation,proofKind=proof.Value.Kind,inspected.Facts,inspected.Preservation,
                    scope="Uncommitted Native stage copied for independent image validation only; not CLI product or device delivery."
                },new JsonSerializerOptions{WriteIndented=true}));
            }
        }
        Assert.Equal(path,Assert.Single(Directory.GetFiles(folder.Path)));
        Assert.Equal(input,File.ReadAllBytes(path));Assert.Equal(originalHash,Hash(File.ReadAllBytes(Source())));
    }

    [Theory]
    [InlineData("aux-orientation")]
    [InlineData("exif-orientation")]
    [InlineData("missing-exif-orientation")]
    [InlineData("tile-transform")]
    [InlineData("unknown-essential")]
    [InlineData("duplicate-transform")]
    [InlineData("primary-clap")]
    [InlineData("aux-clap")]
    [InlineData("invalid-exif-orientation")]
    [InlineData("duplicate-exif-orientation")]
    [Trait("Category","DerivedRealSamples")]
    public async Task AmbiguousOrMismatchedGeometryCannotCreateAnAuthorizedStage(string defect)
    {
        byte[] original=File.ReadAllBytes(Source()),fixtureBase=original;
        using var preparedWorkspace=new MediaWorkspace();
        if(defect is "primary-clap" or "aux-clap")
        {
            // Inserting a property shifts file offsets. Use a Neutral file, whose
            // source motion absolute-pointer fields have already been removed.
            var prepared=await new NeutralMediaService().CreateNeutralBundleAsync(Source(),null,preparedWorkspace);
            fixtureBase=File.ReadAllBytes(prepared.PrimaryImage.Path);
        }
        byte[] changed=Fixture(fixtureBase,defect);
        using var folder=new P9TargetTestInputs.Folder(nameof(AmbiguousOrMismatchedGeometryCannotCreateAnAuthorizedStage)+defect);
        string input=Path.Combine(folder.Path,"input.heic");File.WriteAllBytes(input,changed);
        using var workspace=new MediaWorkspace();bool created=false,reachedStage=false;
        var error=await Record.ExceptionAsync(async ()=>
        {
            var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(input,null,workspace);
            var binding=Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
            reachedStage=true;
            using var transaction=await NativeMediaService.StageHeicGainMapToJpegUltraHdrAsync(bundle.PrimaryImage.Path,
                Path.Combine(folder.Path,"not-published.jpg"),binding,bundle.PrimaryImage.ByteLength,92,HdrOutputPolicy.PreserveHdr);
            created=true;
        });
        Assert.NotNull(error);Assert.False(created);
        _output.WriteLine($"{defect}: reachedNativeStage={reachedStage}; rejection={error.GetType().Name}: {error.Message}");
        if(defect is "primary-clap" or "aux-clap")
        {
            Assert.True(reachedStage,"Crop fixture must reach the actual Native geometry guard.");
            Assert.Contains("no-crop",error.Message);
        }
        Assert.Equal(input,Assert.Single(Directory.GetFiles(folder.Path)));
        Assert.Equal(changed,File.ReadAllBytes(input));Assert.Equal(original,File.ReadAllBytes(Source()));
    }

    private static string Source()=>Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/三星.heic"));
    [Theory]
    [InlineData("mdat-owner")]
    [InlineData("not-final")]
    [InlineData("duplicate-owner")]
    [InlineData("live-inventory")]
    [InlineData("marker-name")]
    [InlineData("overlap")]
    [InlineData("gap")]
    [InlineData("footer")]
    [InlineData("back-offset")]
    [Trait("Category","DerivedRealSamples")]
    public async Task SefMustRemainOneExactOrdinaryFinalMetadataOwner(string defect)
    {
        using var initialWorkspace=new MediaWorkspace();
        var initial=await new NeutralMediaService().CreateNeutralBundleAsync(Source(),null,initialWorkspace);
        byte[] neutral=File.ReadAllBytes(initial.PrimaryImage.Path),changed=(byte[])neutral.Clone();
        var sef=One(Children(changed,0,changed.Length),"sefd");Assert.Equal(changed.Length,sef.End);
        int table=changed.Length-8-checked((int)BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(changed.Length-8)));
        int tag=table-checked((int)BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(table+16)));
        if(defect=="mdat-owner")Encoding.ASCII.GetBytes("mdat").CopyTo(changed,sef.At+4);
        else if(defect=="not-final")changed=changed.Concat(new byte[]{0,0,0,8,102,114,101,101}).ToArray();
        else if(defect=="duplicate-owner")changed=changed.Concat(changed[sef.At..]).ToArray();
        else if(defect=="live-inventory")
        {
            byte[] name=Encoding.ASCII.GetBytes("MotionPhoto_Data"),payload=new byte[8+16+4];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2),0x0a30);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),16);name.CopyTo(payload,8);
            byte[] body=payload.Concat("SEFH"u8.ToArray()).Concat(new byte[8+12+8]).ToArray();int at=payload.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at+4),107);BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at+8),1);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at+14),0x0a30);BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at+16),checked((uint)payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at+20),checked((uint)payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(body.Length-8),24);"SEFT"u8.CopyTo(body.AsSpan(body.Length-4));
            byte[] box=new byte[8+body.Length];BinaryPrimitives.WriteUInt32BigEndian(box,checked((uint)box.Length));"sefd"u8.CopyTo(box.AsSpan(4));body.CopyTo(box,8);
            changed=changed[..sef.At].Concat(box).ToArray();
        }
        else if(defect=="marker-name")BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(table+14),0x0a30);
        else if(defect=="overlap")BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(table+28),BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(table+16)));
        else if(defect=="gap")BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(table+20),BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(table+20))-1);
        else if(defect=="footer")changed[^8]^=1;
        else if(defect=="back-offset")BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(table+16),checked((uint)(table+1)));
        else throw new ArgumentOutOfRangeException(nameof(defect));
        Assert.NotEqual(Hash(neutral),Hash(changed));
        using var folder=new P9TargetTestInputs.Folder(nameof(SefMustRemainOneExactOrdinaryFinalMetadataOwner)+defect);
        string input=Path.Combine(folder.Path,"derived.heic");File.WriteAllBytes(input,changed);
        using var workspace=new MediaWorkspace();bool created=false;
        var error=await Record.ExceptionAsync(async()=>
        {
            var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(input,null,workspace);
            var binding=Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
            using var transaction=await NativeMediaService.StageHeicGainMapToJpegUltraHdrAsync(bundle.PrimaryImage.Path,
                Path.Combine(folder.Path,"not-published.jpg"),binding,bundle.PrimaryImage.ByteLength,92,HdrOutputPolicy.PreserveHdr);
            created=true;
        });
        Assert.NotNull(error);Assert.False(created);Assert.Equal(input,Assert.Single(Directory.GetFiles(folder.Path)));
        _output.WriteLine($"{defect}: rejection={error.GetType().Name}: {error.Message}");
        Assert.Equal(changed,File.ReadAllBytes(input));
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] ReadShared(string path){using var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var m=new MemoryStream();s.CopyTo(m);return m.ToArray();}
    private static byte[] Exif(byte[] bytes)
    {
        Assert.True(NativeHeifBoxParser.TryLocateExifItem(bytes,out long offset,out long length,out string? error),error);
        int start=checked((int)offset);int tiff=checked(start+4+(int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start)));
        return bytes[tiff..checked(start+(int)length)];
    }
    private static byte[] JpegExif(byte[] b)
    {
        for(int p=2;p<b.Length;)
        {
            Assert.Equal(255,b[p]);int marker=b[p+1];Assert.NotEqual(218,marker);int n=BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p+2));
            if(marker==225&&b.AsSpan(p+4,6).SequenceEqual("Exif\0\0"u8))return b[(p+10)..(p+2+n)];p+=2+n;
        }
        throw new InvalidDataException("Missing output Exif.");
    }
    private static (int Width,int Height) JpegDimensions(byte[] b)
    {
        Assert.True(b.AsSpan(0,2).SequenceEqual(new byte[]{255,216}));
        for(int p=2;p<b.Length;)
        {
            Assert.Equal(255,b[p]);int marker=b[p+1];Assert.NotEqual(218,marker);
            int n=BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p+2));Assert.InRange(n,2,b.Length-p-2);
            if(marker is 192 or 193 or 194)
                return(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p+7)),BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p+5)));
            p+=2+n;
        }
        throw new InvalidDataException("Missing JPEG frame header.");
    }
    private sealed record Box(int At,int Body,int End,string Kind);
    private static Box One(IEnumerable<Box> rows,string kind)=>Assert.Single(rows,x=>x.Kind==kind);
    private static List<Box> Children(byte[] b,int first,int end)
    {
        var rows=new List<Box>();
        while(first<end)
        {
            int size=checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(first))),header=8;
            if(size==1){size=checked((int)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(first+8)));header=16;}
            Assert.InRange(size,header,end-first);rows.Add(new(first,first+header,first+size,Encoding.ASCII.GetString(b,first+4,4)));first+=size;
        }
        return rows;
    }
    private static byte[] Fixture(byte[] source,string defect)
    {
        byte[] b=(byte[])source.Clone();var meta=One(Children(b,0,b.Length),"meta");var props=One(Children(b,meta.Body+4,meta.End),"iprp");
        var pc=Children(b,props.Body,props.End);var ipco=One(pc,"ipco");var entries=Children(b,ipco.Body,ipco.End);var ipma=One(pc,"ipma");
        int p=ipma.Body;int version=b[p],flags=(b[p+1]<<16)|(b[p+2]<<8)|b[p+3];uint count=BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p+4));p+=8;
        var associations=new Dictionary<int,List<(int At,int Index)>>();
        for(uint i=0;i<count;i++)
        {
            int w=version==0?2:4;int id=w==2?BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)):checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)));p+=w;int n=b[p++];var values=new List<(int,int)>();
            for(int j=0;j<n;j++){w=(flags&1)!=0?2:1;int value=w==1?b[p]:BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));values.Add((p,value&(w==1?127:32767)));p+=w;}associations.Add(id,values);
        }
        int wide=(flags&1)!=0?2:1;
        void Set((int At,int Index) slot,int index,bool essential=false)
        {
            if(wide==1)b[slot.At]=checked((byte)(index|(essential?128:0)));
            else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(slot.At),checked((ushort)(index|(essential?32768:0))));
        }
        var primary=associations[49];var auxiliary=associations[55];
        var rotation=primary.Single(x=>entries[x.Index-1].Kind=="irot");
        int identity=entries.FindIndex(x=>x.Kind=="irot"&&b[x.Body]==0)+1;Assert.True(identity>0);
        if(defect=="mirror") {var property=entries[rotation.Index-1];Encoding.ASCII.GetBytes("imir").CopyTo(b,property.At+4);b[property.Body]=1;SetExifOrientation(b,2);}
        else if(defect=="aux-orientation")Set(auxiliary.Single(x=>entries[x.Index-1].Kind=="irot"),identity);
        else if(defect=="exif-orientation")SetExifOrientation(b,1);
        else if(defect=="missing-exif-orientation")SetExifOrientation(b,0,true);
        else if(defect=="invalid-exif-orientation")SetExifOrientation(b,9);
        else if(defect=="duplicate-exif-orientation")SetExifOrientation(b,6,duplicate:true);
        else if(defect is "primary-clap" or "aux-clap")
        {
            int index=entries.Count+1;Set((defect=="primary-clap"?primary:auxiliary).Single(x=>entries[x.Index-1].Kind=="pixi"),index);
            byte[] clap=new byte[40];BinaryPrimitives.WriteUInt32BigEndian(clap,40);"clap"u8.CopyTo(clap.AsSpan(4));
            uint[] values={(uint)(defect=="primary-clap"?4000:1000),1,(uint)(defect=="primary-clap"?3000:750),1,0,1,0,1};
            for(int i=0;i<values.Length;i++)BinaryPrimitives.WriteUInt32BigEndian(clap.AsSpan(8+i*4),values[i]);
            int insertion=ipco.End;var iloc=One(Children(b,meta.Body+4,meta.End),"iloc");
            ShiftFileExtents(b,iloc,insertion,clap.Length);
            foreach(var box in new[]{ipco,props,meta})BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(box.At),checked((uint)(box.End-box.At+clap.Length)));
            b=b[..insertion].Concat(clap).Concat(b[insertion..]).ToArray();
        }
        else if(defect=="tile-transform")Set(associations[1].Single(x=>entries[x.Index-1].Kind=="colr"),rotation.Index);
        else if(defect=="duplicate-transform")Set(primary.Single(x=>entries[x.Index-1].Kind=="pixi"),rotation.Index);
        else if(defect=="unknown-essential")
        {
            var unknown=entries[identity-1];Encoding.ASCII.GetBytes("zzzz").CopyTo(b,unknown.At+4);Set(primary.Single(x=>entries[x.Index-1].Kind=="pixi"),identity,true);
        }
        else throw new ArgumentOutOfRangeException(nameof(defect));
        return b;
    }
    private static void ShiftFileExtents(byte[] b,Box iloc,int insertion,int delta)
    {
        int p=iloc.Body,version=b[p];Assert.InRange(version,0,2);p+=4;
        int offsetSize=b[p]>>4,lengthSize=b[p]&15,baseSize=b[p+1]>>4,indexSize=version==0?0:b[p+1]&15;p+=2;
        ulong Read(int size){ulong n=0;for(int j=0;j<size;j++)n=(n<<8)|b[p++];return n;}
        void Write(int at,int size,ulong n){for(int j=size-1;j>=0;j--){b[at+j]=(byte)n;n>>=8;}Assert.Equal(0UL,n);}
        ulong count=Read(version==2?4:2);
        for(ulong i=0;i<count;i++)
        {
            Read(version==2?4:2);ulong method=version==0?0:Read(2)&15;Assert.InRange(method,0UL,1UL);Assert.Equal(0UL,Read(2));
            int baseAt=p;ulong baseOffset=Read(baseSize),extents=Read(2);
            bool shiftedBase=method==0&&baseOffset>=(ulong)insertion;
            if(shiftedBase)Write(baseAt,baseSize,baseOffset+(ulong)delta);
            for(ulong j=0;j<extents;j++)
            {
                if(indexSize>0)Read(indexSize);int at=p;ulong offset=Read(offsetSize);Read(lengthSize);
                if(method==0&&!shiftedBase&&baseOffset+offset>=(ulong)insertion)Write(at,offsetSize,offset+(ulong)delta);
            }
        }
        Assert.Equal(iloc.End,p);
    }
    private static void SetExifOrientation(byte[] b,ushort value,bool remove=false,bool duplicate=false)
    {
        Assert.True(NativeHeifBoxParser.TryLocateExifItem(b,out long offset,out _,out string? error),error);int p=checked((int)offset);int t=p+4+checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)));bool little=b[t]==73;
        uint U32(int at)=>little?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)):BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
        ushort U16(int at)=>little?BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)):BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
        int ifd=t+checked((int)U32(t+4));int count=U16(ifd);
        var tag=Enumerable.Range(0,count).Select(i=>ifd+2+12*i).Single(at=>U16(at)==0x112);
        if(duplicate)
        {
            var other=Enumerable.Range(0,count).Select(i=>ifd+2+12*i).First(at=>U16(at) is not (0x112 or 0x8769 or 0x8825));
            b.AsSpan(tag,12).CopyTo(b.AsSpan(other,12));return;
        }
        if(remove){if(little)BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(tag),0xfffe);else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(tag),0xfffe);}
        else if(little)BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(tag+8),value);else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(tag+8),value);
    }
}
