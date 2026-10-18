"""Build exact existing Native+sidecar against the new pinned multidepth archive."""
import sys
import validate as v

def run(tag):
    work=v.WORK/'hevc-migration'
    config=v.read(v.ROOT/'.ai-tmp/workspace/P7/R1-malformed-mutation-collision/plan135-r1d-msys2-runtime-qualified/22b-plan135-native-configure-result.json')
    cmake=config['executable'];assert v.hashfile(cmake)==config['executableSha256']
    args=config['arguments'][:]
    args[args.index('-B')+1]=str(work/'native-build')
    args=[('-DLPB_NATIVE_OUTPUT_ROOT='+str(v.WORK/'native-artifacts-v5')) if x.startswith('-DLPB_NATIVE_OUTPUT_ROOT=') else x for x in args]
    args=[('-DLPB_P5_R5_CODEC_PREFIX='+str(work/'codecs')) if x.startswith('-DLPB_P5_R5_CODEC_PREFIX=') else x for x in args]
    native=[v.identity(p) for p in sorted((v.ROOT/'LivePhotoBox.Native').rglob('*')) if p.is_file() and p.suffix in ('.cpp','.h','.txt','.json','.cmake','.vcxproj','.filters')]+[v.identity(v.ROOT/'CMakeLists.txt')]
    before=v.read(work/'multidepth-codec-identity.json')
    for item in before['archives']+before['headers']:assert v.identity(v.ROOT/item['path'])==item
    if not (work/'native-build/CMakeCache.txt').exists():v.checked([cmake,*args],work/('configure-native-'+tag),config['environment'])
    for target in ('LivePhotoBoxNative','LivePhotoBoxNativeTestHarness'):
        v.checked([cmake,'--build',work/'native-build','--config','Release','--target',target,'--','/m:1','/p:CL_MPCount=1','/p:UseMultiToolTask=false','/v:minimal'],work/(target+'-'+tag))
    for item in native:assert v.identity(v.ROOT/item['path'])==item
    v.save(v.WORK/('native-v5-'+tag+'.json'),dict(sources=native,codecIdentity=v.identity(work/'multidepth-codec-identity.json'),
        cache=v.identity(work/'native-build/CMakeCache.txt'),artifacts=[v.identity(p) for p in sorted((v.WORK/'native-artifacts-v5').rglob('*')) if p.is_file()]))

if __name__=='__main__':run(sys.argv[1])
