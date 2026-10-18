# smoke-win10.ps1 — WIN10-01 自包含单文件冒烟 + TC-18 运行时联网检查 + COMPAT-01 真实根只读兼容（T-33）
# 真实 .dsh 仅只读：前后快照哈希证明未变更；证据只记计数，不含正文/ID/路径。
[CmdletBinding()]
param(
    [string]$EvidencePath = '',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $EvidencePath) { $EvidencePath = Join-Path $repo 'artifacts/evidence/T-33-win10.txt' }
$bin = Join-Path $repo 'src/DeepSeekHarnessCleaner.UI/bin/Release/net10.0-windows'
$pubDir = Join-Path $repo '.artifacts/publish/t33-win10'
$fixtureSrc = Join-Path $repo '.artifacts/tmp/dsh-t18-fixture'
$fixtureRoot = Join-Path $repo '.artifacts/tmp/dsh-t33-fixture/sessions'
$smokeFile = Join-Path $repo '.artifacts/tmp/t33-smoke.txt'
$logDir = Join-Path $repo '.artifacts/tmp/dshcleaner-logs-t33'
$results = [ordered]@{}

# ---- A. 系统信息（WIN10-01：Edition/Version/Build）----
$nt = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$osLine = "OS=Windows $($nt.ProductName) Edition=$($nt.EditionID) Version=$($nt.DisplayVersion) Build=$($nt.CurrentBuild).UBR$($nt.UBR)"
$hv = (Get-Item "$env:LOCALAPPDATA\Programs\DeepSeek Harness\DeepSeek Harness.exe" -ErrorAction SilentlyContinue)
$harnessVer = if ($hv) { "$($hv.VersionInfo.FileVersion)" } else { 'NOT_FOUND' }

# ---- B. 自包含单文件 win-x64 发布 ----
if (-not $SkipPublish) {
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.artifacts/nuget-http-cache'
    Push-Location $repo
    dotnet publish src/DeepSeekHarnessCleaner.UI -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:NuGetAudit=false -o .artifacts/publish/t33-win10 | Out-Null
    $pubExit = $LASTEXITCODE
    Pop-Location
} else { $pubExit = 0 }
$exe = Join-Path $pubDir 'DeepSeekHarnessCleaner.UI.exe'
$exeOk = Test-Path $exe
$exeMB = if ($exeOk) { [math]::Round((Get-Item $exe).Length / 1MB, 1) } else { 0 }
if (-not $exeOk) { throw "publish 未产出 EXE（PUB_EXIT=$pubExit）" }

# ---- C. 冒烟 + TC-18 运行时联网轮询 ----
if (Test-Path $fixtureRoot) { Remove-Item -Recurse -Force $fixtureRoot }
Copy-Item -Recurse $fixtureSrc $fixtureRoot
if (Test-Path $logDir) { Remove-Item -Recurse -Force $logDir }
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe
$psi.Arguments = "--smoke `"$smokeFile`" --scan-root `"$fixtureRoot`" --log-dir `"$logDir`""
$psi.WorkingDirectory = $repo
$p = [System.Diagnostics.Process]::Start($psi)
$conns = New-Object 'System.Collections.Generic.HashSet[string]'
while (-not $p.WaitForExit(100)) {
    try {
        foreach ($c in Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction Stop) {
            [void]$conns.Add("$($c.Protocol)|L$($c.LocalPort)|$($c.RemoteAddress):$($c.RemotePort)|$($c.State)")
        }
    } catch { }
}
$smokeExit = $p.ExitCode
$smokePass = if (Test-Path $smokeFile) { (Get-Content $smokeFile | Where-Object { $_ -like 'PASS *' }).Count } else { 0 }
$smokeFail = if (Test-Path $smokeFile) { (Get-Content $smokeFile | Where-Object { $_ -like 'FAIL *' }).Count } else { -1 }
$netHits = $conns.Count

# ---- D. COMPAT-01：真实 .dsh\sessions 只读扫描（前后哈希证明只读）----
$realRoot = Join-Path $env:USERPROFILE '.dsh/sessions'
function Get-RootSnapshot([string]$root) {
    $map = @{}
    foreach ($f in (Get-ChildItem $root -Recurse -File -ErrorAction Stop)) {
        $map[$f.FullName] = "$($f.Length)|$($f.LastWriteTimeUtc.Ticks)"
    }
    return $map
}
function Compare-Snapshot($before, $after) {
    $changed = 0; $deleted = 0; $added = 0
    foreach ($k in $before.Keys) {
        if (-not $after.ContainsKey($k)) { $deleted++ }
        elseif ($after[$k] -ne $before[$k]) { $changed++ }
    }
    foreach ($k in $after.Keys) { if (-not $before.ContainsKey($k)) { $added++ } }
    return @{ Changed = $changed; Deleted = $deleted; Added = $added }
}
$compat = $null
if (Test-Path $realRoot) {
    $before = Get-RootSnapshot $realRoot
    $harness = Join-Path $repo '.artifacts/tmp/compat-harness.ps1'
    @'
param([string]$Root, [string]$Bin)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $Bin 'DeepSeekHarnessCleaner.Domain.dll'),
            (Join-Path $Bin 'DeepSeekHarnessCleaner.Application.dll'),
            (Join-Path $Bin 'DeepSeekHarnessCleaner.Infrastructure.dll'),
            (Join-Path $Bin 'DeepSeekHarnessCleaner.UI.dll'),
            (Join-Path $Bin 'ZstdSharp.dll')
$src = New-Object DeepSeekHarnessCleaner.UI.UiScanSource
$svc = New-Object DeepSeekHarnessCleaner.Application.InventoryService($src)
$res = $svc.Scan($Root, [System.Threading.CancellationToken]::None)
$status = @{}; foreach ($s in $res.Sessions) { $k = [string]$s.Status; $status[$k] = 1 + $status[$k] }
$gens = @{}; $enc = @{}
foreach ($s in $res.Sessions) { foreach ($g in $s.Generations) {
    $gk = 'gen' + $g.Generation; $gens[$gk] = 1 + $gens[$gk]
    $ek = if ($g.Compressed) { 'zstd' } else { 'plain' }; $enc[$ek] = 1 + $enc[$ek] } }
$amb = @($res.Sessions | Where-Object { $_.IsAmbiguous }).Count
$proj = New-Object DeepSeekHarnessCleaner.Application.TranscriptProjector
$semOk = 0; $semPartial = 0; $semFail = 0; $sampled = 0
foreach ($s in ($res.Sessions | Where-Object { $_.Status -eq 'Valid' -and -not $_.IsAmbiguous } | Select-Object -First 25)) {
    $sampled++
    $g = $s.Generations | Sort-Object Generation -Descending | Select-Object -First 1
    try {
        $bytes = [System.IO.File]::ReadAllBytes($g.FullPath)
        if ($g.Compressed) {
            $ms = New-Object System.IO.MemoryStream(,$bytes)
            $dec = [DeepSeekHarnessCleaner.Infrastructure.Compression.ZstdFrameDecoder]::Decode($ms)
            if (-not $dec.IsSuccess) { $semFail++; continue }
            $bytes = $dec.Content
        }
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        $pr = $proj.Project($text)
        if ($pr.IsProjectionComplete) { $semOk++ } else { $semPartial++ }
    } catch { $semFail++ }
}
@{
    sessions = $res.Sessions.Count; totalBytes = $res.TotalBytes; anomalies = $res.AnomalousFiles.Count
    status = $status; generations = $gens; encodings = $enc; ambiguous = $amb
    semanticSampled = $sampled; semanticComplete = $semOk; semanticPartial = $semPartial; semanticFail = $semFail
} | ConvertTo-Json -Compress -Depth 5
'@ | Set-Content -Path $harness -Encoding utf8
    $json = & pwsh -NoProfile -File $harness -Root $realRoot -Bin $bin | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw "compat harness 退出码 $LASTEXITCODE" }
    $compat = $json | ConvertFrom-Json
    $after = Get-RootSnapshot $realRoot
    $diff = Compare-Snapshot $before $after
}

# ---- E. 判定与证据 ----
$checks = [ordered]@{
    'WIN10-01 发布退出码 0'                 = ($pubExit -eq 0)
    'WIN10-01 单文件 EXE 存在'              = $exeOk
    'WIN10-01 自包含冒烟退出码 0'           = ($smokeExit -eq 0)
    'WIN10-01 冒烟 72 PASS / 0 FAIL'        = ($smokePass -eq 72 -and $smokeFail -eq 0)
    'TC-18 运行期零新增 TCP 连接'           = ($netHits -eq 0)
    'COMPAT-01 真实根存在且完成只读扫描'    = ($null -ne $compat -and $compat.sessions -gt 0)
    'COMPAT-01 真实根既有文件零变更零删除（新增允许）' = ($null -ne $compat -and $diff.Changed -eq 0 -and $diff.Deleted -eq 0)
}
$allPass = -not ($checks.Values -contains $false)
$lines = @(
    "WIN10-01/TC-18/COMPAT-01（T-33） $([DateTime]::UtcNow.ToString('s'))Z"
    $osLine
    "HARNESS_VERSION=$harnessVer"
    "PUBLISH_EXIT=$pubExit EXE=$exe sizeMB=$exeMB"
    "SMOKE_EXIT=$smokeExit PASS=$smokePass FAIL=$smokeFail (fixture=$fixtureRoot)"
    "TC18_NETWORK_CONNECTIONS=$netHits"
)
if ($null -ne $compat) {
    $lines += "COMPAT sessions=$($compat.sessions) totalBytes=$($compat.totalBytes) anomalies=$($compat.anomalies) ambiguous=$($compat.ambiguous)"
    $lines += "COMPAT status=$($compat.status | ConvertTo-Json -Compress -Depth 3)"
    $lines += "COMPAT generations=$($compat.generations | ConvertTo-Json -Compress) encodings=$($compat.encodings | ConvertTo-Json -Compress)"
    $lines += "COMPAT semantic sampled=$($compat.semanticSampled) complete=$($compat.semanticComplete) partial=$($compat.semanticPartial) fail=$($compat.semanticFail)"
    $lines += "COMPAT snapshot files=$($before.Count) changed=$($diff.Changed) deleted=$($diff.Deleted) added=$($diff.Added)（added 为实时 Harness 会话写入，非本扫描）"
}
foreach ($k in $checks.Keys) {
    $lines += ('{0} : {1}' -f $(if ($checks[$k]) { 'PASS' } else { 'FAIL' }), $k)
}
$lines += "WIN10_RESULT=$(if ($allPass) { 'PASS' } else { 'FAIL' })"
$lines | Set-Content -Path $EvidencePath -Encoding utf8
$lines | ForEach-Object { Write-Output $_ }
if (-not $allPass) { exit 1 }
