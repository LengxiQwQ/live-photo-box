"""Offline existing-DLL comparison, no production dispatch or codec implementation."""
import json
import shutil
import sys
import validate as v

ROOT = v.WORK / 'backend-comparison'
SOURCES = {
    'hevc-exact-av': ROOT / 'apple-metadata-disabled.mov',
    'hevc-av': ROOT / 'apple-av-only.mov',
    'hevc': v.ROOT / '.ai-tmp/cache/samples/苹果双文件.MOV',
    'h264': v.WORK / 'soak-v11/case/retained/codecs/sdr.mp4',
    'hdr': v.ROOT / '.ai-tmp/workspace/P5-R5/huawei-extraction-pass-c/motion.mp4',
    'vivo': v.ROOT / '.ai-tmp/cache/samples/vivo双文件.mp4',
}


def run(label, backend, source_name, target, container, repetitions='24', quiet='60', runtime_name='driver-v19'):
    assert target in ('h264','hevc') and container in ('mov','mp4'), 'Use explicit codec/container names; numeric values are not accepted by the C++ host.'
    source = SOURCES[source_name]
    dest = ROOT / label
    probe = ROOT / ('build/Release/BackendProbe.exe' if runtime_name == 'driver-v19' else 'build-v2/Release/BackendProbe.exe')
    dll = v.WORK / runtime_name / ('LivePhotoBox.Video.Libav.dll' if backend == 'libav' else 'LivePhotoBox.Native.dll')
    before = v.identity(source)
    runtime = [v.identity(dll), v.identity(probe)]
    metadata = ROOT / ('input-profile-' + source_name)
    if not metadata.exists():
        v.checked([shutil.which('ffprobe'), '-v', 'error', '-show_streams', '-show_format', '-show_packets',
                   '-show_data_hash', 'sha256', '-of', 'json', source], metadata)
    assert v.read(metadata / 'process.json')['directChildExitCode'] == 0
    profile = v.read(metadata / 'stdout.txt')
    mebx = sum(s.get('codec_tag_string') == 'mebx' for s in profile['streams'])
    temp = v.WORK / 't/backend-comparison' / label
    temp.mkdir(parents=True, exist_ok=False)
    result = v.command([probe, backend, dll, source, dest / 'outputs', target, container,
                        str(int(bool(mebx))), repetitions, quiet], dest / 'process',
                       {'TEMP': str(temp), 'TMP': str(temp)}, timeout=3600)
    assert before == v.identity(source)
    for item in runtime:
        assert v.hashfile(v.ROOT / item['path']) == item['sha256']
    rows = [json.loads(line) for line in (dest / 'process/stdout.txt').read_text(encoding='utf-8-sig').splitlines()]
    conversions = [x for x in rows if x['kind'] == 'conversion']
    measurements = [x for x in rows if x['kind'] == 'resources' and x['phase'] == 'after-release']
    quiescent = [x for x in rows if x['kind'] == 'resources' and x['phase'] == 'quiet']
    outputs = [v.identity(p) for p in sorted((dest / 'outputs').glob('*')) if p.is_file()]
    unexpected_temp = [v.identity(p) for p in temp.rglob('*') if p.is_file()]
    summary = dict(diagnosticOnly=True, productionDispatchChanged=runtime_name != 'driver-v19', process=result,
                   hostAnnotationScope='The old host productionDispatchChanged=false annotation means this harness does not modify dispatch; candidate production source can differ from run3. Runtime/source binding determines policy.',
                   backend=backend, source=before, sourceKind=source_name,
                   target=target, container=container, runtime=runtime,
                   scope='Actual Native public transcode ABI (owner from diagnostics) or actual sidecar transcode ABI. Same C++ measurement host, CRF23, software, same source,3 warmups. Raw sidecar output is StageA; Native MEBX transplant/final publish exercised only via public Native calls.',
                   inputMebxStreamCount=mebx,
                   deferredMebx=bool(mebx and backend == 'libav'), conversions=conversions,
                   resourceRows=rows, measuredResources=measurements,
                   quietSemaphoreValues=sorted(set(x['semaphores'] for x in quiescent)),
                   retainedOutputs=outputs, unexpectedTempFiles=unexpected_temp,
                   allRequestsSucceeded=bool(conversions) and all(x['status'] == 0 for x in conversions),
                   statuses=sorted(set(x['status'] for x in conversions)))
    v.save(dest / 'summary.json', summary)
    print(json.dumps({k: summary[k] for k in ('backend', 'sourceKind', 'target', 'container', 'allRequestsSucceeded', 'statuses', 'quietSemaphoreValues')}, ensure_ascii=True))
    assert result['directChildExitCode'] == 0 and len(conversions) == int(repetitions) + 3
    assert not unexpected_temp


if __name__ == '__main__':
    run(*sys.argv[1:])
