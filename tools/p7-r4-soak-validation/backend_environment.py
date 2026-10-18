import pathlib
import shutil
import backend_compare as b
import validate as v

def run():
    for label,script in [
        ('environment-hevc', "Get-AppxPackage '*HEVC*' | Select-Object Name,Version | ConvertTo-Json"),
        ('environment-os', 'Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber | ConvertTo-Json')]:
        v.checked(['powershell','-NoProfile','-Command',script],b.ROOT/label)
    probe=pathlib.Path(shutil.which('ffprobe'))
    v.checked([probe,'-version'],b.ROOT/'ffprobe-version')
    v.save(b.ROOT/'validation-tools.json',dict(ffprobe=v.identity(probe),
        pyavInstalledUnder='.ai-tmp/cache/backend-validation-python',
        productionDependency=False,ffmpegExeInvoked=False))

if __name__=='__main__':run()
