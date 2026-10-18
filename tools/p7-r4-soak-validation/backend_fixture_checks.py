"""Verify experimental fixture equivalence and PCM packet rechunking separately."""
import hashlib
import json
import backend_verify as b
import validate as v

def payload(profile,stream,path):
    digest=hashlib.sha256();total=0
    with path.open('rb') as f:
        packets=b.p5.stream_packets(profile,stream)
        for p in packets:
            f.seek(int(p['pos'])); data=f.read(int(p['size'])); assert len(data)==int(p['size'])
            digest.update(data);total+=len(data)
    return dict(packetCount=len(packets),bytes=total,sha256=digest.hexdigest().upper())

def run():
    root=b.b.ROOT
    src=b.b.SOURCES['hevc'];fixture=b.b.SOURCES['hevc-exact-av']
    original=src.read_bytes();changed=fixture.read_bytes()
    assert len(original)==len(changed)
    offsets=[2779397,2780557,2781358]
    expected=bytearray(original)
    for offset in offsets:
        assert expected[offset:offset+4]==b'trak'
        expected[offset:offset+4]=b'free'
    assert expected==changed
    a=v.read(root/'input-profile-hevc/stdout.txt');z=v.read(root/'input-profile-hevc-exact-av/stdout.txt')
    matches=[]
    for kind in ('video','audio'):
        x=b.p5.one_stream(a,kind);y=b.p5.one_stream(z,kind)
        keys=['pts','dts','duration','size','data_hash','flags']
        xp=[p for p in a['packets'] if p['stream_index']==x['index']]
        yp=[p for p in z['packets'] if p['stream_index']==y['index']]
        assert [{k:p.get(k) for k in keys} for p in xp]==[{k:p.get(k) for k in keys} for p in yp]
        assert x['time_base']==y['time_base']
        matches.append(dict(kind=kind,packetCount=len(xp),packetPayloadPtsDtsDurationFlagsExact=True))
    audio=[]
    for label in ('qualify-libav-exact-h264','qualify-libav-exact-hevc','repeat-libav-exact-h264','repeat-libav-exact-hevc'):
        d=v.read(root/label/'summary.json');s=v.read(root/'decoded-source-hevc-exact-av/stdout.txt')
        baseline=payload(s,b.p5.one_stream(s,'audio'),fixture)
        for item in d['retainedOutputs']:
            path=v.ROOT/item['path'];p=v.read(root/label/('probe-'+path.name)/'stdout.txt')
            actual=payload(p,b.p5.one_stream(p,'audio'),path)
            assert actual['sha256']==baseline['sha256'] and actual['bytes']==baseline['bytes']
            audio.append(dict(output=item,source=baseline,outputPayload=actual,exactConcatenatedPcmBytes=True,
                              packetBoundaryIdentity=False,explanation='MOV repacketization changes three packet boundaries; ordered concatenated PCM bytes remain identical.'))
    v.save(root/'fixture-and-pcm-proof.json',dict(source=v.identity(src),fixture=v.identity(fixture),
        recipe='Experimental fixture only: three parsed moov/trak track IDs 3/4/5 type fields changed from trak to free. All other bytes, offsets, file size and AV packet timing/payload unchanged. Not a neutral-media product output or metadata preservation proof.',
        typeOffsets=offsets,allOtherBytesExact=True,avMatches=matches,pcmProof=audio))
    print(json.dumps(dict(fixtureExact=True,pcmOutputsVerified=len(audio))))

if __name__=='__main__': run()
