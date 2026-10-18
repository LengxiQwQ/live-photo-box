"""Offline independent demux/decode and streaming container checks for R4."""
import json, pathlib, shutil, struct, sys
import validate as v

def boxes(path):
    rows=[];size=path.stat().st_size
    with path.open('rb') as f:
        offset=0
        while offset<size:
            f.seek(offset);header=f.read(8);assert len(header)==8
            length,kind=struct.unpack('>I4s',header);header_size=8
            if length==1:length=struct.unpack('>Q',f.read(8))[0];header_size=16
            elif length==0:length=size-offset
            assert length>=header_size and offset+length<=size,(path,offset,length)
            rows.append(dict(offset=offset,bytes=length,headerBytes=header_size,type=kind.decode('ascii')))
            offset+=length
        assert offset==size
    return rows

def probe(path,dest,packets=False):
    args=[shutil.which('ffprobe'),'-v','error','-show_streams','-show_format','-of','json']
    if packets:args.extend(['-show_packets','-show_data_hash','sha256'])
    v.checked(args+[path],dest)
    return v.read(dest/'stdout.txt')

def essence(probe):
    eligible={s['index']:s['codec_type'] for s in probe['streams'] if s['codec_type'] in ('video','audio')}
    return [(eligible[p['stream_index']],p['size'],p['data_hash']) for p in probe['packets'] if p['stream_index'] in eligible]

def run(label,tag,large_label='large-supported-v11'):
    dest=v.WORK/('independent-'+tag);soak=v.WORK/label/'case'
    retained=soak/'retained'
    rows=[]
    for i,path in enumerate(sorted(retained.rglob('*'))):
        if path.suffix.lower() not in ('.jpg','.jpeg','.heic','.mov','.mp4'):continue
        out=dest/('media-'+str(i))
        facts=probe(path,out/'probe',True);videos=[s for s in facts['streams'] if s['codec_type']=='video'];assert videos
        v.checked([shutil.which('ffmpeg'),'-v','error','-xerror','-i',path,'-map','0:v:0','-f','null','-'],out/'video-decode')
        empty_audio=[]
        for stream in [s for s in facts['streams'] if s['codec_type']=='audio']:
            if not stream.get('codec_name'):
                assert not any(p['stream_index']==stream['index'] for p in facts['packets']), 'Unknown nonempty audio stream cannot be independently decoded.'
                empty_audio.append(dict(index=stream['index'],packetCount=0,codecTag=stream.get('codec_tag_string')))
                continue
            v.checked([shutil.which('ffmpeg'),'-v','error','-xerror','-i',path,'-map','0:'+str(stream['index']),'-f','null','-'],out/('audio-decode-'+str(stream['index'])))
        rows.append(dict(file=v.identity(path),streams=[dict(codec=s.get('codec_name'),type=s['codec_type']) for s in facts['streams']],emptyAudioTracks=empty_audio,passed=True))
    assert len(rows)>=29,len(rows)
    source=v.ROOT/'.ai-tmp/cache/samples/苹果双文件.MOV'
    original=probe(source,dest/'large-original',True)
    large=[]
    for name,path in [('supported',v.WORK/'fixtures/supported.mov'),('over4g',v.WORK/'fixtures/over4g.mov'),('cleaned',v.WORK/large_label/'case/video.mov')]:
        hierarchy=boxes(path);facts=probe(path,dest/('large-'+name),True)
        assert essence(original)==essence(facts),(name,'media packet identity changed')
        v.checked([shutil.which('ffmpeg'),'-v','error','-xerror','-i',path,'-map','0:v:0','-map','0:a?','-f','null','-'],dest/('large-decode-'+name))
        large.append(dict(file=v.identity(path),topLevelBoxes=hierarchy,mediaPackets=len(essence(facts)),packetIdentityUnchanged=True,independentDecode=True))
    v.save(dest/'summary.json',dict(passed=True,retained=rows,large=large,
        scope='Offline independent primary image / audiovisual decode; top-level ISO hierarchy and audiovisual compressed packet identity for large derived fixtures. Not a new production >32bit successful extraction claim.'))
    print(json.dumps(dict(passed=True,retained=len(rows),large=len(large))))

if __name__=='__main__':run(*sys.argv[1:])
