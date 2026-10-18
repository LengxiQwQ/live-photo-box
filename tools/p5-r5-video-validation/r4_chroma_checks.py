"""Offline real-derived non-left HEVC chroma-position regression.

ffmpeg only changes source SPS metadata for a named derived fixture; all tested
transcodes go through the existing Core/Native C++ sidecar product route.
"""
import json
import pathlib
import shutil
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/p7-r4-soak-validation'))
import validate as v


def run(tag, phase='fixed'):
    work = v.WORK / 'hevc-migration'
    destination = work / ('chroma-' + phase + '-' + tag)
    assert not destination.exists()
    # A prior real product HEVC encode has no B-picture/edit-list preroll.
    # Stream-copying the original Apple's preroll through the external BSF
    # changes packet timing, so that unsuitable first fixture is preserved as
    # a failed tool qualification and is not used for this regression.
    originals = list((ROOT / '.ai-tmp/workspace/P5-R5/p7-r4-hevc-v2-matrix-v59/routes/apple-hevc-to-hevc-sdr-mp4').glob('*.mp4'))
    assert len(originals) == 1
    original = originals[0]
    before = v.identity(original)
    driver = work / ('route-driver-' + tag) / 'P5R5.RouteDriver.dll'
    runtime = [v.identity(p) for p in driver.parent.glob('*') if p.is_file()]
    rows = []
    for number, location in ((1, 'center'), (2, 'topleft')):
        source = destination / (location + '.mov')
        v.checked([shutil.which('ffmpeg'), '-v', 'error', '-i', original, '-map', '0:v:0', '-map', '0:a?',
                   '-c', 'copy', '-bsf:v', 'hevc_metadata=chroma_sample_loc_type=' + str(number), source],
                  destination / (location + '-derive'))
        def probe(path, label):
            result = destination / label
            v.checked([shutil.which('ffprobe'), '-v', 'error', '-show_streams', '-of', 'json', path], result)
            return v.read(result / 'stdout.txt')
        input_profile = probe(source, location + '-source-probe')
        video = next(s for s in input_profile['streams'] if s['codec_type'] == 'video')
        assert video['chroma_location'] == location
        input_identity = v.identity(source)
        for codec in ('h264', 'hevc'):
            name = location + '-' + codec
            output_parent = destination / name
            v.checked([v.helper.DOTNET, driver, 'route', 'apple-hevc-to-' + codec + '-sdr-mp4', source, output_parent],
                      destination / (name + '-convert'))
            outputs = list(output_parent.rglob('*.mp4'))
            assert len(outputs) == 1, outputs
            output = outputs[0]
            profile = probe(output, name + '-output-probe')
            actual = next(s for s in profile['streams'] if s['codec_type'] == 'video')
            assert actual['codec_name'] == codec
            equal = actual.get('chroma_location') == location
            if phase == 'fixed': assert equal, (name, actual.get('chroma_location'))
            elif phase.startswith('before'): assert not equal, 'Expected old candidate to reproduce the omission.'
            else: raise ValueError(phase)
            v.checked([shutil.which('ffmpeg'), '-v', 'error', '-xerror', '-i', output, '-map', '0:v:0', '-f', 'null', '-'],
                      destination / (name + '-decode'))
            assert input_identity == v.identity(source)
            rows.append({'source': input_identity, 'output': v.identity(output), 'expected': location,
                         'actual': actual.get('chroma_location'), 'preserved': equal, 'codec': codec})
    assert before == v.identity(original)
    for item in runtime: assert item == v.identity(ROOT / item['path'])
    v.save(destination / 'summary.json', {'passed': phase == 'fixed' and all(r['preserved'] for r in rows),
           'reproducedBeforeFix': phase.startswith('before') and all(not r['preserved'] for r in rows),
           'category': 'Real-derived SPS-only fixture; not an original non-left manufacturer sample.',
           'original': before, 'runtime': runtime, 'rows': rows})
    print(json.dumps({'phase': phase, 'routes': len(rows), 'preserved': [r['preserved'] for r in rows]}))


if __name__ == '__main__': run(*sys.argv[1:])
