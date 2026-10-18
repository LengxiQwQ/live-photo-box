"""Independent checks of final R4 retained codec outputs; never a production backend."""
import importlib.util
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('p5_frozen_checks', pathlib.Path(__file__).with_name('validate.py'))
p5 = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = p5
spec.loader.exec_module(p5)
sys.path.insert(0, str(ROOT / 'tools/p7-r4-soak-validation'))
import validate as v


def run(label, tag):
    destination = v.WORK / 'hevc-migration' / ('output-checks-' + label)
    destination.mkdir(parents=True, exist_ok=False)
    (destination / 'raw').mkdir()
    matrix = v.read(ROOT / ('.ai-tmp/workspace/P5-R5/p7-r4-hevc-v2-matrix-' + tag) / 'report.json')
    assert matrix['state'] == 'matrix-pass'
    tools = matrix['tools']
    for tool in tools.values():
        assert v.hashfile(tool['path']) == tool['sha256']
    manifest = p5.verify_active_manifest(ROOT, 'P7-R4-HEVC-v2')[0]
    runner = p5.Runner(ROOT, destination, manifest, pathlib.Path(tools['ffmpeg']['path']),
        pathlib.Path(tools['ffprobe']['path']), v.helper.DOTNET, pathlib.Path(), [], {}, {}, tools)
    source_files = {
        'sdr': ROOT / '.ai-tmp/cache/samples/苹果双文件.MOV',
        'hdr': ROOT / '.ai-tmp/workspace/P5-R5/huawei-extraction-pass-c/motion.mp4',
        'hevc8': ROOT / '.ai-tmp/cache/samples/苹果双文件.MOV',
        'mf': ROOT / '.ai-tmp/cache/samples/vivo双文件.mp4'
    }
    retained = v.WORK / label / 'case/retained/codecs'
    rows = []
    for name, source in source_files.items():
        output = retained / (name + '.mp4')
        identities = [v.identity(source), v.identity(output)]
        before = runner.probe(source, name + '-source')
        after = runner.probe(output, name + '-output')
        source_video, output_video = p5.one_stream(before, 'video'), p5.one_stream(after, 'video')
        source_mebx = p5.mebx_streams(before)
        if source_mebx:
            mebx = p5.compare_mebx(before, after)
        else:
            assert not p5.mebx_streams(after), 'Output invented a MEBX track.'
            mebx = {'sourceTracks': 0, 'outputTracks': 0, 'equal': True}
        row = {'name': name, 'files': identities, 'container': p5.check_container(after, 'mp4'),
            'timing': p5.compare_reencoded_frames(before, after), 'audio': p5.compare_audio(before, after),
            'rotationEqual': p5.rotation_degrees(source_video) == p5.rotation_degrees(output_video),
            'mebx': mebx,
            'staticMetadata': p5.compare_static_metadata(source, output, source_video, output_video),
            'decodedHdrMetadata': p5.compare_hdr_frame_metadata(before, after)}
        assert row['rotationEqual']
        p5.decode_all(runner, output, name + '-decode')
        if name == 'hdr':
            assert output_video['codec_name'] == 'hevc' and output_video['pix_fmt'] == 'yuv420p10le'
            row['profile'] = p5.huawei_hdr_profile(source_video, output_video)
        else:
            source_video, row['independentCicp'] = p5.reconcile_independent_cicp(
                runner, name, source, output, source_video, output_video)
            row['profile'] = p5.sdr_profile(source_video, output_video)
            assert output_video['codec_name'] == ('hevc' if name == 'hevc8' else 'h264')
            row['frozenReferenceMetrics'] = p5.compare_quality(runner, source, output, output_video, name + '-quality')
            # Timing has its own unchanged independent gate above. Also compare
            # picture ordinals: MF 100ns vs source 90kHz rounding can make an
            # ffmpeg framesync filter select the preceding output picture.
            # This supplements the frozen metric; it does not replace it or
            # retime any product output or alter any acceptance threshold.
            argv = row['frozenReferenceMetrics']['command']['argv']
            argv = [arg.replace('setpts=PTS-STARTPTS', 'settb=1/1000,setpts=N*1000') for arg in argv]
            command, _, stderr = runner.require_command_success(name + '-picture-ordinal-quality', argv)
            text = stderr.decode('utf-8', 'replace')
            psnr = re.findall(r'PSNR[^\r\n]*?average:([^\s]+)', text, re.I)
            ssim = re.findall(r'SSIM[^\r\n]*?All:([^\s]+)', text, re.I)
            assert psnr and ssim
            row['pictureOrdinalMetrics'] = {'command': command, 'psnrAverage': psnr[-1], 'ssimAll': ssim[-1],
                'supplementalOnly': True, 'numericAcceptanceThresholdApplied': False}
        if name != 'mf':
            row['copiedAudioBytes'] = p5.compare_copied_audio_payload(before, after, source, output)
        else:
            assert 'baseline' in output_video['profile'].lower()
            row['audioPolicy'] = 'MF audio is reencoded; independent stream/duration/decode evidence is not packet-copy evidence.'
        assert identities == [v.identity(source), v.identity(output)]
        row['passed'] = True
        rows.append(row)
    v.save(destination / 'summary.json', {'passed': True, 'sourceTool': v.identity(pathlib.Path(__file__)),
        'frozenValidator': v.identity(pathlib.Path(__file__).with_name('validate.py')), 'routes': rows,
        'commands': runner.command_records, 'tools': tools})
    print(json.dumps({'passed': True, 'routes': [row['name'] for row in rows]}))


if __name__ == '__main__':
    run(*sys.argv[1:3])
