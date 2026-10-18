"""Offline FFmpeg stream/semantic cross-checks, never imports product parsers."""
import json, pathlib, shutil, re

def vui(v,path,destination):
    v.run_checked([shutil.which('ffmpeg'),'-v','info','-i',path,'-map','0:v:0','-frames:v','1',
        '-c','copy','-bsf:v','trace_headers','-f','null','-'],destination)
    log=(destination/'stderr.txt').read_text(encoding='utf-8',errors='strict')
    values=[]
    for field in ('colour_primaries','transfer_characteristics','matrix_coefficients','video_full_range_flag'):
        found={int(x) for x in re.findall(r'\b'+field+r'\s+[01]+\s+=\s+(\d+)',log)}
        assert len(found)<=1,(str(path),field,found)
        values.append(found.pop() if found else None)
    return tuple(values)

def effective_color(declared,encoded):
    effective=[]
    for i in range(4):
        d=declared[i] if declared is not None else None
        e=encoded[i]
        if i<3:
            if d==2:d=None
            if e==2:e=None
        if d is not None and e is not None:assert int(d)==int(e),(declared,encoded)
        effective.append(e if e is not None else d)
    assert all(x is not None for x in effective),(declared,encoded)
    return tuple(effective)

def nclx(path):
    data=pathlib.Path(path).read_bytes()
    def boxes(start,end,visual=False):
        while start<end:
            # QuickTime visual sample descriptions may terminate extension
            # atoms with a four-byte zero word (frozen Apple MOV does so).
            if visual and end-start==4 and data[start:end]==b'\0\0\0\0':return
            assert end-start>=8
            size=int.from_bytes(data[start:start+4],'big');kind=data[start+4:start+8];header=8
            if size==1: size=int.from_bytes(data[start+8:start+16],'big');header=16
            elif size==0: size=end-start
            assert size>=header and start+size<=end
            yield kind,start+header,start+size
            start+=size
    found=[]
    def walk(start,end,visual=False):
        for kind,body,limit in boxes(start,end,visual):
            if kind in (b'moov',b'trak',b'mdia',b'minf',b'stbl'):walk(body,limit)
            elif kind==b'stsd':walk(body+8,limit)
            elif kind in (b'avc1',b'avc3',b'hvc1',b'hev1'):walk(body+78,limit,True)
            elif kind==b'colr' and data[body:body+4]==b'nclx':
                assert limit-body>=11
                found.append(tuple(int.from_bytes(data[body+i:body+i+2],'big') for i in (4,6,8))+(bool(data[body+10]&128),))
    walk(0,len(data));assert len(found)<=1
    return found[0] if found else None

def inspect(v, source, destination):
    v.run_checked([shutil.which('ffprobe'),'-v','error','-show_streams','-show_format','-of','json',source],destination)
    return v.read(destination/'stdout.txt')

def video_stream(probe):
    streams=[s for s in probe['streams'] if s['codec_type']=='video']
    assert len(streams)==1
    return streams[0]

def rotation(stream):
    rotations=[s['rotation'] for s in stream.get('side_data_list',[]) if 'rotation' in s]
    return rotations[0] % 360 if rotations else int(stream.get('tags',{}).get('rotate',0)) % 360

def verify(v, name, mode, source, output, destination, truth):
    before=inspect(v,source,destination/'input')
    after=inspect(v,output,destination/'output')
    a,b=video_stream(before),video_stream(after)
    assert int(a['width'])==int(b['width']) and int(a['height'])==int(b['height'])
    assert rotation(a)==rotation(b)
    if mode.startswith('image-'):
        assert b['codec_name']==('hevc' if mode=='image-heic' else 'mjpeg')
        if mode=='image-copy':assert v.hashfile(source)==v.hashfile(output)
    else:
        codec=a['codec_name'] if mode=='remux' else ('hevc' if mode in ('hdr','sdr-hevc') else 'h264')
        assert b['codec_name']==codec
        assert 'mp4' in after['format']['format_name'].split(',')
        ad=float(a['duration']);bd=float(b['duration']);assert ad>0 and bd>0
        # Existing approved frame-interval tolerance; no new performance SLA.
        fps=eval_fraction(a.get('avg_frame_rate','0/1'))
        assert abs(ad-bd)<=max(0.002,1.0/fps if fps>0 else 0.002)
        audio_a=[s for s in before['streams'] if s['codec_type']=='audio']
        audio_b=[s for s in after['streams'] if s['codec_type']=='audio']
        assert len(audio_a)==len(audio_b)
        for x,y in zip(audio_a,audio_b):
            if mode in ('hdr','remux') or truth.get('BackendName') == 'minimal-libav':
                for field in ('codec_name','sample_rate','channels'):assert x.get(field)==y.get(field)
            else:
                # The frozen MF route decodes PCM and encodes AAC. Its contract
                # requires an audio stream and timing, not byte/profile identity.
                assert y['codec_name']=='aac' and int(y['sample_rate'])>0 and int(y['channels'])>0
                assert float(y['duration'])>0
                assert abs(float(x['duration'])-float(y['duration']))<=0.1
        source_nclx=nclx(source);output_nclx=nclx(output)
        source_vui=vui(v,source,destination/'input-vui');output_vui=vui(v,output,destination/'output-vui')
        source_color=effective_color(source_nclx,source_vui)
        output_color=effective_color(output_nclx,output_vui)
        assert source_color==output_color,(name,source_color,output_color)
        for field in ('color_primaries','color_transfer','color_space','color_range'):
            if a.get(field) is not None:
                assert a.get(field)==b.get(field),(name,field,a.get(field),b.get(field))
            else:
                # FFprobe can omit source sample-entry NCLX transfer data. In
                # that case require exact independent structural NCLX equality.
                assert source_color==output_color,(name,field)
        if mode in ('hdr','remux'):assert a['pix_fmt']==b['pix_fmt']
        if mode=='hdr':assert b['profile']=='Main 10' and b['pix_fmt']=='yuv420p10le'
        # ffprobe's MOV/MP4 demuxer family is not an exact container identity.
        with open(output,'rb') as stream:
            header=stream.read(32)
        assert header[4:8]==b'ftyp' and header[8:12]!=b'qt  '
    v.run_checked([shutil.which('ffmpeg'),'-v','error','-xerror','-i',output,'-map','0:v:0','-f','null','-'],destination/'decode')
    assert (destination/'decode/stderr.txt').stat().st_size==0
    if not mode.startswith('image-') and audio_b:
        v.run_checked([shutil.which('ffmpeg'),'-v','error','-xerror','-i',output,'-map','0:a:0','-f','null','-'],destination/'decode-audio')
        assert (destination/'decode-audio/stderr.txt').stat().st_size==0
    v.save(destination/'semantic-result.json',dict(passed=True,source=v.identity(source),output=v.identity(output),inputStream=a,outputStream=b,truth=truth))

def eval_fraction(s):
    n,d=map(int,s.split('/'));return n/d if d else 0
