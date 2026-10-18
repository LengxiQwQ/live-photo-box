"""Capture the normal Release build entry plus same-config test harness."""
import pathlib
import sys
import validate as v

def run(tag):
    work=v.WORK/'hevc-migration'
    before=v.sources()
    extra=[v.identity(v.ROOT/p) for p in ('CMakeLists.txt',
        'scripts/native/build-native.ps1','scripts/native/build-p5-r5-video-sidecar-prerequisites.ps1',
        'scripts/native/build-p7-r4-hevc-prerequisites.ps1',
        'tools/p5-r5-video-validation/profiles/P7-R4-HEVC-v2/manifest.json')]
    result=v.command(['pwsh','-NoProfile','-ExecutionPolicy','Bypass','-File',
        v.ROOT/'scripts/native/build-native.ps1','-Configuration','Release',
        '-ExecutionRecordId','r4-hevc-'+tag],work/('release-'+tag),timeout=3600)
    assert result['directChildExitCode']==0
    cmake=pathlib.Path('C:/Program Files/Microsoft Visual Studio/18/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe')
    v.checked([cmake,'--build',v.ROOT/'.ai-tmp/workspace/P5-R5/cmake-build','--config','Release',
        '--target','LivePhotoBoxNativeTestHarness','--','/m:1','/p:CL_MPCount=1','/p:UseMultiToolTask=false','/v:minimal'],work/('release-harness-'+tag))
    assert before==v.sources()
    for item in extra:assert item==v.identity(v.ROOT/item['path'])
    v.save(work/('release-candidate-'+tag+'.json'),dict(sources=before,buildInputs=extra,
        artifacts=[v.identity(p) for p in sorted((v.ROOT/'artifacts/native').rglob('*')) if p.is_file()]))

if __name__=='__main__':run(sys.argv[1])
