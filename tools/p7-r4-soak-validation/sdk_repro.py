"""Run the SDK-only diagnostic and preserve input/executable/source identities."""
import json
import sys
import validate as v


def run(label, source, mode):
    source = (v.ROOT / source).resolve()
    exe = v.WORK / 'sdk-repro/build/Release/MfSdkLifecycleRepro.exe'
    before = v.identity(source)
    code = [v.identity(p) for p in sorted((v.ROOT / 'tools/p7-r4-soak-validation/sdk-repro').glob('*')) if p.is_file()]
    executable = v.identity(exe)
    dest = v.WORK / 'sdk-repro' / label
    process = v.command([exe, source, '24', mode, '60'], dest / 'process', timeout=600)
    rows = [json.loads(line) for line in (dest / 'process/stdout.txt').read_text(encoding='utf-8-sig').splitlines()]
    assert before == v.identity(source) and executable == v.identity(exe)
    releases = [r for r in rows if r['kind'] == 'after-release']
    quiet = [r for r in rows if r['kind'] == 'quiet']
    functional = process['directChildExitCode'] == 0 and len(releases) == 24 and all(r['hr'] >= 0 and r['decodedSamples'] > 0 for r in releases)
    value = dict(diagnosticOnly=True, productCodeLinked=False,
                 scope='Windows SDK-only source reader decode; no LivePhotoBox headers, Native/managed libraries, encoder, sidecar, media parser or workspace.',
                 source=before, sourceFiles=code, executable=executable, process=process,
                 mode=mode, successfulDecodeIterations=len(releases), functionalAssertionsPassed=functional,
                 rows=rows, semaphoreAfterRelease=[r['semaphores'] for r in releases],
                 quietSemaphoreValues=sorted(set(r['semaphores'] for r in quiet)),
                 resourceAcceptance='Observations only; functionally successful decoding is not a resource PASS or R4 acceptance.')
    v.save(dest / 'summary.json', value)
    print(json.dumps({k: value[k] for k in ('mode', 'successfulDecodeIterations', 'functionalAssertionsPassed', 'semaphoreAfterRelease', 'quietSemaphoreValues')}, ensure_ascii=True))
    assert functional


if __name__ == '__main__':
    run(*sys.argv[1:4])
