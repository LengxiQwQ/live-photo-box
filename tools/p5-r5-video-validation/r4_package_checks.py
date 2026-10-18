"""Offline normal Release build/package identity capture, no publication."""
import pathlib
import sys
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / 'tools/p7-r4-soak-validation'))
import validate as v
from build_hevc_codecs import visual_studio_tools


def run(tag):
    work = v.WORK / 'hevc-migration'
    out = work / ('release-package-' + tag)
    v.checked([v.helper.DOTNET, 'build', 'LivePhotoBox.Core/LivePhotoBox.Core.csproj', '-c', 'Release',
               '-p:Platform=x64', '-p:OutDir=' + str(out), '--verbosity', 'minimal'],
              work / ('release-package-' + tag + '-build'))
    files = [v.identity(p) for p in sorted(out.rglob('*')) if p.is_file()]
    assert not any(p['path'].lower().endswith(('/ffmpeg.exe', '/ffprobe.exe')) for p in files)
    for name in ('LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll'):
        assert v.hashfile(out / name) == v.hashfile(v.NATIVE / 'Release/win-x64' / name)
    licenses = {'FFmpeg-COPYING.GPLv3': '8CEB4B9EE5ADEDDE47B31E975C1D90C73AD27B6B165A1DCD80C7C545EB65B903',
                'x264-copyright.txt': '32B1062F7DA84967E7019D01AB805935CAA7AB7321A7CED0E30EBE75E5DF1670',
                'x265-copyright.txt': 'D8AFB1BCC7A2CFC603683B168D6987EF0A48E59E0DA3693BF55C5D33B67E2B49'}
    for name, digest in licenses.items():
        assert v.hashfile(out / 'licenses/LivePhotoBox.Video.Libav' / name) == digest
    _, vc = visual_studio_tools()
    for name in ('LivePhotoBox.Native.dll', 'LivePhotoBox.Video.Libav.dll'):
        v.checked([vc / 'dumpbin.exe', '/dependents', out / name], work / ('imports-' + tag + '-' + name))
    v.save(work / ('release-package-' + tag + '.json'), {'passed': True, 'productionBuildEntry': True,
           'files': files, 'noCodecCli': True,
           'scope': 'Actual normal Core Release build plus shipped Native/sidecar and notices; not MSIX distribution acceptance or Windows N verification.'})


if __name__ == '__main__': run(sys.argv[1])
