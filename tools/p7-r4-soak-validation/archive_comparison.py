"""Create-only R4 backend diagnostic archive, never an acceptance marker."""
import json
import pathlib
import shutil
import backend_compare as b
import validate as v

def run():
    dest=v.ROOT/'.ai-tmp/evidence/P7/P7-R4-large-batch-soak/run-3'
    assert not dest.exists()
    baseline=v.read(b.ROOT/'baseline.json')
    for item in baseline['sources']+baseline['runtime']:
        assert v.identity(v.ROOT/item['path'])==item
    assert (b.ROOT/'independent-review.md').is_file(), 'Fresh review required before sealing'
    dest.mkdir()
    payloads=[]
    def copy(source, relative):
        before=v.identity(source);target=dest/relative
        target.parent.mkdir(parents=True,exist_ok=True)
        with source.open('rb') as src,target.open('xb') as out:shutil.copyfileobj(src,out)
        assert before==v.identity(source)
        assert before['sha256']==v.hashfile(target)
        payloads.append(dict(path=target.relative_to(dest).as_posix(),source=before,
                             bytes=target.stat().st_size,sha256=v.hashfile(target)))
    for source in sorted(b.ROOT.rglob('*')):
        if not source.is_file():continue
        relative=source.relative_to(b.ROOT)
        if relative.parts[0] in ('build','build-v2'):
            if source.name not in ('BackendProbe.exe','CMakeCache.txt'):continue
        copy(source,pathlib.Path('raw')/relative)
    for source in sorted((v.ROOT/'tools/p7-r4-soak-validation').glob('backend_*.py')):
        copy(source,pathlib.Path('tools')/source.name)
    copy(v.ROOT/'tools/p7-r4-soak-validation/archive_comparison.py',pathlib.Path('tools/archive_comparison.py'))
    for source in sorted((v.ROOT/'tools/p7-r4-soak-validation/backend-comparison').glob('*')):
        if source.is_file():copy(source,pathlib.Path('tools/backend-comparison')/source.name)
    for item in baseline['runtime']:
        copy(v.ROOT/item['path'],pathlib.Path('runtime')/pathlib.Path(item['path']).name)
    for name in ('hevc','h264','hdr','vivo'):
        copy(b.SOURCES[name],pathlib.Path('inputs')/b.SOURCES[name].name)
    for source in sorted((v.WORK/'sdk-repro/backend-comparison-h264').rglob('*')):
        if source.is_file():copy(source,pathlib.Path('sdk-h264-control')/source.relative_to(v.WORK/'sdk-repro/backend-comparison-h264'))
    report=v.ROOT/'.ai-tmp/evidence/P7/P7-R4-large-batch-soak/documentation/P7-R4-MF-libav-backend-comparison.md'
    copy(report,pathlib.Path('context')/report.name)
    index=dict(phase='P7',task='P7-R4-large-batch-soak',run=3,
        status='BACKEND_COMPARISON_COMPLETE_CORRECTNESS_AND_RESOURCE_BLOCKERS_R4_OPEN',
        r4Accepted=False,productionBackendChanged=False,workerImplemented=False,
        head=baseline['head'],branch=v.helper.git('branch','--show-current').strip(),
        roadmapRevision='317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7',
        unchangedProductionIdentityCount=len(baseline['sources'])+len(baseline['runtime']),
        scope='Existing actual DLL ABI transcodes, same-process repetitions, independent output observations. Raw sidecar StageA does not prove complete product semantics.',
        findings=['MF HEVC per-call Semaphore retention and green product pixels',
                  'libav flat Semaphore/Handle but PrivateBytes growth and missing custom AVIO buffer release',
                  'libav SDR8 HEVC output actual Main10 despite ABI depth8',
                  'Original multiple-MEBX input unsupported; Native SDR StageB integration not implemented',
                  'MF H264 input requests rejected by existing Native timing gate'],
        limitations=['Not a repaired candidate or R4 mixed soak acceptance','No production architecture approval',
                     'No ffmpeg.exe invocation; PyAV pixel metric is diagnostic, not frozen P5 PSNR/SSIM',
                     'Parallel groups are not an isolated performance benchmark'],payloads=payloads)
    v.save(dest/'evidence-index.json',index)
    for item in payloads:
        assert v.hashfile(dest/item['path'])==item['sha256']
    print(json.dumps(dict(payloadCount=len(payloads),indexSha256=v.hashfile(dest/'evidence-index.json'),allHashesVerified=True)))

if __name__=='__main__':run()
