"""Independent ffprobe checks for diagnostic backend outputs; no ffmpeg.exe calls.

Reuses frozen P5 timing/audio/container assertions, does not grant P5 acceptance.
"""
import importlib.util
import json
import pathlib
import shutil
import sys
import backend_compare as b
import validate as v

spec = importlib.util.spec_from_file_location('p5_video_checks', v.ROOT / 'tools/p5-r5-video-validation/validate.py')
p5 = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = p5
spec.loader.exec_module(p5)

def probe(path, dest):
    if not dest.exists():
        v.checked([shutil.which('ffprobe'), '-v', 'error', '-show_streams', '-show_format',
                   '-show_packets', '-show_frames', '-show_data_hash', 'sha256', '-of', 'json', path], dest)
    return v.read(dest / 'stdout.txt')

def verify(label, suffix=''):
    root = b.ROOT / label
    summary = v.read(root / 'summary.json')
    source = probe(v.ROOT / summary['source']['path'], b.ROOT / ('decoded-source-' + summary['sourceKind']))
    results = []
    for item in summary['retainedOutputs']:
        path = v.ROOT / item['path']
        output = probe(path, root / ('probe-' + path.name))
        src = p5.one_stream(source, 'video')
        dst = p5.one_stream(output, 'video')
        checks = {}
        def check(name, action):
            try:
                checks[name] = dict(passed=True, result=action())
            except Exception as error:
                checks[name] = dict(passed=False, error=str(error))
        def codec():
            assert dst['codec_name'] == summary['target']
            return dst['codec_name']
        def color():
            keys = ['color_range', 'color_primaries', 'color_transfer', 'color_space']
            assert all(src.get(k) == dst.get(k) for k in keys)
            return {k: dst.get(k) for k in keys}
        def depth():
            assert src['pix_fmt'] == dst['pix_fmt'] or {src['pix_fmt'], dst['pix_fmt']} <= {'yuv420p', 'yuvj420p'}
            return dict(source=src['pix_fmt'], output=dst['pix_fmt'])
        def rotation():
            assert p5.rotation_degrees(src) == p5.rotation_degrees(dst)
            return p5.rotation_degrees(dst)
        def audio_packets():
            a=[s for s in source['streams'] if s['codec_type']=='audio']
            z=[s for s in output['streams'] if s['codec_type']=='audio']
            assert len(a)==len(z)
            for s,t in zip(a,z):
                assert s['codec_name']==t['codec_name']
                assert [p5.packet_hash(p) for p in p5.stream_packets(source,s)] == [p5.packet_hash(p) for p in p5.stream_packets(output,t)]
            return dict(exact=True)
        check('codec', codec)
        check('bitDepth', depth)
        check('CICP', color)
        check('rotation', rotation)
        check('container', lambda: p5.check_container(output, summary['container']))
        check('frameTiming', lambda: p5.compare_reencoded_frames(source, output))
        check('audioTiming', lambda: p5.compare_audio(source, output))
        check('audioPacketCopy', audio_packets)
        if summary['backend'] != 'libav' and p5.mebx_streams(source):
            check('MEBX', lambda: p5.compare_mebx(source, output))
        if summary['backend'] != 'libav':
            check('staticMetadata', lambda: p5.compare_static_metadata(v.ROOT / summary['source']['path'], path, src, dst))
            check('copiedAudioPayload', lambda: p5.compare_copied_audio_payload(source, output, v.ROOT / summary['source']['path'], path))
        results.append(dict(output=item, checks=checks, stream=dst,
                            decodedFrames=len(p5.frame_facts(output)[1]),
                            stderr=(root / ('probe-' + path.name) / 'stderr.txt').read_text(encoding='utf-8-sig')))
    record=dict(label=label, results=results,
                limitation='Diagnostic checks only. Native route checks include real StageB MEBX/static metadata readback; raw sidecar output has no StageB. No pixel PSNR/SSIM, cancellation proof, broad matrix or phase acceptance. ffprobe full frame enumeration is used; ffmpeg.exe is never called.')
    v.save(root / ('independent' + suffix + '.json'), record)
    print(json.dumps({r['output']['path']: {k:x['passed'] for k,x in r['checks'].items()} for r in results},ensure_ascii=True))

if __name__ == '__main__':
    verify(*sys.argv[1:])
