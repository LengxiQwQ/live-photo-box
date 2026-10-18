"""Synthetic HEVC pixel-layout boundaries, existing public product API only.

Run serially AFTER the frozen media/soak campaign. ffmpeg.exe creates explicit
synthetic inputs and independently validates outputs; production never invokes it.
"""
import json
import pathlib
import re
import shutil
import sys
from fractions import Fraction
import r4_closeout as c
from r4_output_checks import p5

v = c.v


def run(tag='v63', revision=''):
    h = c.H / ('pixel-layout-' + tag + revision)
    driver = c.W / ('driver-' + tag)
    # All expectations are fixed before observing any output.
    formats = [('gray', 8, 0), ('yuv422p', 8, 2), ('yuv444p', 8, 3),
               ('gray10le', 10, 0), ('yuv422p10le', 10, 2), ('yuv444p10le', 10, 3),
               ('yuv420p12le', 12, 1)]
    plan = [dict(format=fmt, depth=depth, chroma=chroma, codec=codec,
                 expectedSuccess=depth <= 10)
            for fmt, depth, chroma in formats for codec in ('hevc', 'h264')]
    production = v.read(c.W / ('driver-' + tag + '.json'))['runtime']
    c.verify(production)
    v.save(h / 'plan.json', dict(category='Synthetic profile boundaries, not original RealSamples',
        cases=plan, productRuntime=production,
        capabilityBasis='Pinned x264 X264_BIT_DEPTH=0/chroma=0 and FFmpeg libx264 pix_fmts_all cover 8/10 bit; x265 API8+10 linked. Neither encoder offers exact12bit.',
        quality='Record PSNR/SSIM; frozen parent profile establishes no numeric SLA.'))
    tool = v.ROOT / 'tools/p5-r5-video-validation/r4-pixel-layout-probe'
    r = v.command([v.helper.DOTNET, 'build', tool / 'PixelLayoutProbe.csproj', '-c', 'Release',
        '-p:CandidateRuntime=' + str(driver), '-p:OutDir=' + str(h / 'runtime'), '--verbosity', 'minimal'], h / 'build', timeout=600)
    assert r['directChildExitCode'] == 0 and not r.get('timedOut')
    for item in production:
        source = v.ROOT / item['path']; target = h / 'runtime' / source.relative_to(driver)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        assert v.hashfile(target) == item['sha256']
    runtime = [v.identity(p) for p in sorted((h / 'runtime').rglob('*')) if p.is_file()]
    v.save(h / 'runtime.json', runtime)
    matrix = v.read(v.ROOT / ('.ai-tmp/workspace/P5-R5/p7-r4-hevc-v2-matrix-' + tag + '/report.json'))
    ffmpeg, ffprobe = (matrix['tools'][name]['path'] for name in ('ffmpeg', 'ffprobe'))
    for name in ('ffmpeg', 'ffprobe'):
        assert v.hashfile(matrix['tools'][name]['path']) == matrix['tools'][name]['sha256']
    v.save(h / 'tools.json', [v.identity(pathlib.Path(p)) for p in (ffmpeg, ffprobe)])
    temp = h / 'temp'; temp.mkdir(exist_ok=False)
    env = {'TEMP': str(temp), 'TMP': str(temp)}

    def checked(args, destination):
        record = v.command(args, destination, env, timeout=300)
        assert record['directChildExitCode'] == 0 and not record.get('timedOut'), str(destination)

    def probe(path, destination):
        checked([ffprobe, '-v', 'error', '-select_streams', 'v:0', '-show_streams', '-show_frames', '-of', 'json', path], destination)
        return v.read(destination / 'stdout.txt')

    class CicpRunner:
        def __init__(self): self.ffmpeg = ffmpeg
        def require_command_success(self, label, args):
            destination = h / label
            checked(args, destination)
            return (v.read(destination / 'process.json'),
                    (destination / 'stdout.txt').read_bytes(),
                    (destination / 'stderr.txt').read_bytes())

    cicp_runner = CicpRunner()

    def coded(path, destination):
        checked([ffmpeg, '-v', 'info', '-i', path, '-map', '0:v:0', '-c:v', 'copy',
                 '-bsf:v', 'trace_headers', '-f', 'null', '-'], destination)
        log = (destination / 'stderr.txt').read_text(encoding='utf-8-sig')
        result = {}
        for name in ('chroma_format_idc', 'bit_depth_luma_minus8', 'bit_depth_chroma_minus8'):
            values = set(map(int, re.findall(r'\b' + name + r'\b[^\n]*=\s*(\d+)', log)))
            assert len(values) == 1, (path, name, values)
            result[name] = next(iter(values))
        return result

    rows = []
    for fmt, depth, chroma in formats:
        source = h / (fmt + '.mp4')
        # MOV muxing monochrome signals full range; make SPS agree explicitly.
        color_range = 'pc' if chroma == 0 else 'tv'
        x265_range = 'full' if chroma == 0 else 'limited'
        checked([ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=160x96:rate=24',
            '-frames:v', '6', '-vf', 'format=' + fmt, '-an', '-c:v', 'libx265',
            '-x265-params', 'pools=2:frame-threads=1:bframes=0:lossless=1:colorprim=1:transfer=1:colormatrix=1:range=' + x265_range,
            '-tag:v', 'hvc1', '-movflags', '+write_colr',
            '-color_range', color_range, '-color_primaries', 'bt709', '-color_trc', 'bt709',
            '-colorspace', 'bt709', source], h / (fmt + '-derive'))
        before = v.identity(source)
        source_probe = probe(source, h / (fmt + '-source-probe'))
        source_coded = coded(source, h / (fmt + '-source-coded'))
        assert source_coded == dict(chroma_format_idc=chroma, bit_depth_luma_minus8=depth-8, bit_depth_chroma_minus8=depth-8)
        source_video = source_probe['streams'][0]
        assert [source_video.get(name) for name in ('color_range', 'color_primaries', 'color_transfer', 'color_space')] == [color_range, 'bt709', 'bt709', 'bt709']
        checked([c.H / 'native-regressions/Release/HevcNativeRegressions.exe', 'profile', source], h / (fmt + '-native-profile'))
        assert v.read(h / (fmt + '-native-profile/stdout.txt'))['valid'], 'Synthetic source must satisfy existing Native profile before testing its conversion.'
        for codec in ('hevc', 'h264'):
            metrics = {}
            case = h / (fmt + '-' + codec)
            checked([v.helper.DOTNET, h / 'runtime/PixelLayoutProbe.dll', source, case / 'outputs', codec], case / 'execute')
            report = v.read(case / 'execute/stdout.txt'); result = report['result']; truth = result['ExecutionRecord']['Truth']
            expected = depth <= 10
            assert result['Success'] is expected, (fmt, codec, result)
            assert not truth['FallbackOccurred'] and not truth['FallbackAllowed']
            if not expected:
                assert truth['FailureCategory'] == 'Unsupported', result
                assert not report['entries'] and result['OutputArtifact'] is None
            else:
                assert truth['BackendName'] == 'minimal-libav'
                assert len(report['entries']) == 1
                output = pathlib.Path(result['OutputArtifact']['Path'])
                actual_probe = probe(output, case / 'output-probe')
                actual_coded = coded(output, case / 'output-coded')
                assert actual_coded == source_coded
                a, b = source_probe['streams'][0], actual_probe['streams'][0]
                assert b['codec_name'] == codec and (b['width'], b['height']) == (160, 96)
                _, metrics['independentCicp'] = p5.reconcile_independent_cicp(
                    cicp_runner, fmt + '-' + codec, source, output, a, b)
                for field in ('color_range', 'color_primaries', 'color_transfer', 'color_space'):
                    # Preserve the merged ffprobe view; actual NCLX/VUI evidence
                    # above is mandatory even when the decoder omits a field.
                    if b.get(field) is not None:
                        assert a.get(field) == b[field], (fmt, codec, field, a.get(field), b[field])
                frames_a, frames_b = source_probe['frames'], actual_probe['frames']
                assert len(frames_a) == len(frames_b) == 6
                tick = max(Fraction(a['time_base']), Fraction(b['time_base']))
                timestamps = lambda frames, stream: [Fraction(f['best_effort_timestamp']) * Fraction(stream['time_base']) for f in frames]
                sa, sb = timestamps(frames_a, a), timestamps(frames_b, b)
                assert all(abs((x-sa[0])-(y-sb[0])) <= tick for x, y in zip(sa, sb))
                checked([ffmpeg, '-v', 'error', '-xerror', '-i', output, '-map', '0:v:0', '-f', 'null', '-'], case / 'decode')
                for metric in ('psnr', 'ssim'):
                    checked([ffmpeg, '-v', 'info', '-noautorotate', '-i', source, '-noautorotate', '-i', output,
                        '-filter_complex', '[0:v]format=yuv444p16le[ref];[1:v]format=yuv444p16le[out];[out][ref]' + metric + '=shortest=1',
                        '-an', '-f', 'null', '-'], case / metric)
                    text = (case / metric / 'stderr.txt').read_text(encoding='utf-8-sig')
                    pattern = r'average:([\d.]+|inf)' if metric == 'psnr' else r'All:([\d.]+)'
                    found = re.findall(pattern, text)
                    assert found, (fmt, codec, metric)
                    metrics[metric] = found[-1]
            assert before == v.identity(source)
            rows.append(dict(format=fmt, depth=depth, chroma=chroma, codec=codec, expectedSuccess=expected,
                passed=True, source=before, metrics=metrics, report=v.identity(case / 'execute/stdout.txt')))
    c.verify(production + runtime)
    assert not any(p.is_file() for p in temp.rglob('*'))
    v.save(h / 'summary.json', dict(passed=True, category='Synthetic additional profile coverage', rows=rows,
        runtime=runtime, noProductionCodecCli=True, tempFiles=[], notResourceAcceptance=True))
    print({'passed': True, 'cases': len(rows)})


if __name__ == '__main__': run(*sys.argv[1:])
