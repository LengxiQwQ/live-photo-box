$msixPath = "D:\Projects\live-photo-box\LivePhotoBox\AppPackages\LivePhotoBox_1.14.10.0_x64_Test\LivePhotoBox_1.14.10.0_x64.msix"

# 1. 查看签名证书
Write-Host "=== 签名证书 ==="
$sig = Get-AuthenticodeSignature $msixPath
$sig.SignerCertificate | Format-List Subject, Issuer, Thumbprint, NotBefore, NotAfter
Write-Host "签名状态: $($sig.Status)"
Write-Host ""

# 2. 查看包清单身份信息
Write-Host "=== 包清单 ==="
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($msixPath)
$entry = $zip.GetEntry("AppxManifest.xml")
$stream = $entry.Open()
$reader = New-Object System.IO.StreamReader($stream)
$xml = [xml]$reader.ReadToEnd()
$reader.Close(); $stream.Close(); $zip.Close()
Write-Host "包名称: $($xml.Package.Identity.Name)"
Write-Host "发布者: $($xml.Package.Identity.Publisher)"
Write-Host "版本:   $($xml.Package.Identity.Version)"
Write-Host ""

# 3. 计算包系列名称
Write-Host "=== 包系列名称 ==="
$pub = $xml.Package.Identity.Publisher
$name = $xml.Package.Identity.Name
$hash = [System.Security.Cryptography.HashAlgorithm]::Create("SHA256")
$bytes = $hash.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($pub))
$pubId = [System.Convert]::ToBase64String($bytes).Replace("/","_").Replace("+","-").Replace("=","").Substring(0,13)
Write-Host "$($name)_$pubId"
