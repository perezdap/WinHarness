$content = Get-Content -Raw -Path "$PSScriptRoot\report-content.html"
$tempPath = Join-Path $env:TEMP "wh-arch-review.html"
Set-Content -Path $tempPath -Value $content -Encoding UTF8
Write-Output $tempPath
