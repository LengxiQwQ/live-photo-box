"""Pinned x265 4.3 static 8+10 build, following upstream Windows multilib recipe.

No source/installed archive in the old P5/P7 evidence prefixes is modified.
"""
import hashlib
import os
import pathlib
import shutil
import subprocess
import sys
import importlib.util
_spec=importlib.util.spec_from_file_location('native_build_support',pathlib.Path(__file__).with_name('build-support.py'))
v=importlib.util.module_from_spec(_spec);_spec.loader.exec_module(v)

WORK=v.ROOT/'artifacts/deps/video'
def visual_studio_tools():
    locator=pathlib.Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)'))/'Microsoft Visual Studio/Installer/vswhere.exe'
    installation=pathlib.Path(subprocess.check_output([str(locator),'-latest','-products','*','-requires',
        'Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath'],text=True).strip())
    assert installation.is_dir(), 'Visual Studio C++ toolchain is required.'
    versions=[p for p in (installation/'VC/Tools/MSVC').iterdir() if (p/'bin/Hostx64/x64/lib.exe').is_file()]
    assert versions, 'Visual Studio static library tool is required.'
    vc=max(versions,key=lambda p:tuple(int(part) for part in p.name.split('.')))/'bin/Hostx64/x64'
    bundled=installation/'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    cm=bundled if bundled.is_file() else pathlib.Path(shutil.which('cmake') or '')
    assert cm.is_file(), 'CMake is required.'
    return cm, vc
DOWNLOADS=pathlib.Path(os.environ.get('VCPKG_DOWNLOADS',str(pathlib.Path(os.environ['LOCALAPPDATA'])/'vcpkg/downloads')))
ARCHIVE=DOWNLOADS/'Multicorewareinc-x265-4.3.tar.gz'
SHA='270D0DB180ECEBFC5C2F1FE6451BE66042FAB69273D073428F4AF00420FA0F8B792ECDC71EC1A15EE2860E3B6A325C56295143F117E94FA3616F0CCECB2DED75'
OLD=None

def run():
    global OLD
    if len(sys.argv)>1: OLD=pathlib.Path(sys.argv[1]).resolve()
    assert hashlib.sha512(ARCHIVE.read_bytes()).hexdigest().upper()==SHA
    identity=WORK/'multidepth-codec-identity.json'
    if identity.exists():
        existing=v.read(identity)
        assert existing['sourceSha512']==SHA and existing['profile']=='P7-R4-HEVC-v2'
        for item in existing['archives']+existing['headers']:assert v.identity(v.ROOT/item['path'])==item
        print('X265_8_10_EXISTING_IDENTITY_VERIFIED')
        return
    cm, vc=visual_studio_tools()
    source=WORK/'x265-source'
    ready=WORK/'x265-source-ready.json'
    if source.exists() and not ready.exists():
        # An interrupted extraction/patch must never become a valid source tree.
        assert source.resolve().parent==WORK.resolve()
        attempt=1
        while source.with_name('x265-source-incomplete-'+str(attempt)).exists():attempt+=1
        source.rename(source.with_name('x265-source-incomplete-'+str(attempt)))
    if not source.exists():
        source.mkdir(parents=True)
        v.checked(['tar','-xf',ARCHIVE,'--strip-components=1','-C',source],WORK/'extract-x265')
        overlay=v.ROOT/'build/native/video/dependencies/overlay-ports/x265'
        patches=['disable-install-pdb.patch','version.patch','linkage.diff','pkgconfig.diff','pthread.diff','compiler-target.diff','neon.diff','advapi32.patch']
        for p in patches:
            v.checked(['git','apply','--directory='+source.relative_to(v.ROOT).as_posix(),overlay/p],WORK/('patch-'+p.replace('.','-')))
        v.save(ready,dict(sourceSha512=SHA,files=[v.identity(p) for p in sorted(source.rglob('*')) if p.is_file()]))
    state=v.read(ready)
    assert state['sourceSha512']==SHA
    assert state['files']==[v.identity(p) for p in sorted(source.rglob('*')) if p.is_file()], 'x265 source tree changed after verified extraction/patch'
    nasms=sorted((DOWNLOADS/'tools/nasm').rglob('nasm.exe'))
    assert nasms, 'vcpkg NASM build tool is missing.'
    nasm=nasms[-1]
    vs_version=subprocess.check_output([str(pathlib.Path(os.environ['ProgramFiles(x86)'])/'Microsoft Visual Studio/Installer/vswhere.exe'),'-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationVersion'],text=True).strip().split('.')[0]
    generator={'18':'Visual Studio 18 2026','17':'Visual Studio 17 2022'}[vs_version]
    common=['-G',generator,'-A','x64','-DENABLE_SHARED=OFF','-DENABLE_CLI=OFF',
            '-DENABLE_LIBNUMA=OFF','-DSTATIC_LINK_CRT=ON','-DMAIN12=OFF','-DVERSION=4.3',
            '-DNASM_EXECUTABLE='+str(nasm),'-DCMAKE_DISABLE_FIND_PACKAGE_VLD=ON']
    ten=WORK/'x265-10';eight=WORK/'x265-8';prefix=WORK/'codecs'
    for label,build,args in [('10',ten,['-DHIGH_BIT_DEPTH=ON','-DEXPORT_C_API=OFF']),
                              ('8',eight,['-DHIGH_BIT_DEPTH=OFF','-DEXPORT_C_API=ON','-DLINKED_10BIT=ON',
                                          '-DEXTRA_LIB='+str(ten/'Release/x265-static.lib')])]:
        if not (build/'CMakeCache.txt').exists():
            v.checked([cm,'-S',source/'source','-B',build,*common,*args],WORK/('configure-x265-'+label))
        log=WORK/('build-x265-'+label)
        result=v.command([cm,'--build',build,'--config','Release','--target','x265-static','--','/m:4','/v:minimal'],log,timeout=3600)
        if result['directChildExitCode']!=0:raise RuntimeError('x265 build failed; see '+str(log))
    lib=prefix/'lib';inc=prefix/'include';lib.mkdir(parents=True,exist_ok=True);inc.mkdir(exist_ok=True)
    # Without a completed identity, an existing archive may be a partial lib.exe
    # output. Always combine into a fresh attempt and publish only after success.
    combined=WORK/'x265-combined.lib';attempt=0
    while combined.exists():
        attempt+=1;combined=WORK/('x265-combined-attempt-'+str(attempt)+'.lib')
    v.checked([vc/'lib.exe','/nologo','/ignore:4006','/ignore:4221','/OUT:'+str(combined),
               eight/'Release/x265-static.lib',ten/'Release/x265-static.lib'],WORK/'combine-x265')
    os.replace(combined,lib/'x265-static.lib')
    for src,dst in [(source/'source/x265.h',inc/'x265.h'),(eight/'x265_config.h',inc/'x265_config.h'),
                    (OLD/'lib/libx264.lib',lib/'libx264.lib'),(OLD/'include/x264.h',inc/'x264.h'),
                    (OLD/'include/x264_config.h',inc/'x264_config.h')]:
        if not dst.exists():
            with src.open('rb') as a,dst.open('xb') as z:shutil.copyfileobj(a,z)
        assert v.hashfile(src)==v.hashfile(dst)
    v.save(WORK/'multidepth-codec-identity.json',dict(profile='P7-R4-HEVC-v2',
        source=v.identity(ARCHIVE),sourceSha512=SHA,recipe='Pinned upstream vc18 multilib recipe,8+10 subset,no CLI/shared/12bit; MT runtime; merged archives',
        archives=[v.identity(p) for p in sorted(lib.glob('*.lib'))],headers=[v.identity(p) for p in sorted(inc.glob('*'))],
        x264ReusedFromExactArchive=v.identity(OLD/'lib/libx264.lib')))
    print('X265_8_10_BUILD_COMPLETE')

if __name__=='__main__':run()
