# perf.ps1 — PERF-01：1000 小会话（≤200MB）性能计时（T-33）
# 仅使用项目内 fixture（.artifacts/tmp/dsh-perf-fixture），绝不触碰真实 .dsh。
# 度量：完整元数据初扫 ≤15s；筛选响应 ≤200ms；扫描进程峰值工作集 <512MiB。
[CmdletBinding()]
param(
    [int]$SessionCount = 1000,
    [string]$FixtureRoot = '',
    [string]$EvidencePath = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $FixtureRoot)   { $FixtureRoot   = Join-Path $repo '.artifacts/tmp/dsh-perf-fixture/sessions' }
if (-not $EvidencePath)  { $EvidencePath  = Join-Path $repo 'artifacts/evidence/T-33-perf.txt' }
$bin = Join-Path $repo 'src/DeepSeekHarnessCleaner.UI/bin/Release/net10.0-windows'
if (-not (Test-Path (Join-Path $bin 'DeepSeekHarnessCleaner.UI.dll'))) {
    throw 'UI Release 输出缺失：先运行 dotnet build -c Release'
}

# ---- 机器信息（PERF-01 要求记录 CPU/SSD/Build）----
$nt = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$osLine = "OS=Windows $($nt.ProductName) $($nt.DisplayVersion) Build=$($nt.CurrentBuild).$($nt.UBR) (OSVersion=$([Environment]::OSVersion.Version))"
$cpuLine = "CPU=$($env:PROCESSOR_IDENTIFIER) Cores=$($env:NUMBER_OF_PROCESSORS)"
$diskLine = (Get-PhysicalDisk | ForEach-Object { "$($_.FriendlyName)/$($_.MediaType)/$($_.BusType)" }) -join '; '

# ---- 生成 1000 小会话 fixture（v4 明文 JSONL，每会话 201 行 ≈20KB，总 ~20MB）----
if (Test-Path $FixtureRoot) { Remove-Item -Recurse -Force $FixtureRoot }
$projects = 'projA','projB','projC','projD','projE'
$genSw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 1; $i -le $SessionCount; $i++) {
    $proj = $projects[($i - 1) % 5]
    $id = 'perf-{0:d4}' -f $i
    $dir = Join-Path (Join-Path $FixtureRoot $proj) $id
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $day = 1 + ($i % 27)
    $month = 1 + ([int][Math]::Floor($i / 27) % 9)
    $created = '2026-{0:d2}-{1:d2}T08:00:00+00:00' -f $month, $day
    $lines = New-Object string[] 201
    $lines[0] = '{"type":"header","sessionId":"__ID__","formatVersion":4,"createdAt":"__CREATED__"}'.Replace('__ID__', $id).Replace('__CREATED__', $created)
    for ($k = 1; $k -le 200; $k++) {
        $role = if ($k % 2 -eq 1) { 'user' } else { 'assistant' }
        $lines[$k] = '{"type":"message","role":"__ROLE__","content":"perf sample line __K__ for session __ID__"}'.Replace('__ROLE__', $role).Replace('__K__', $k).Replace('__ID__', $id)
    }
    [System.IO.File]::WriteAllLines((Join-Path $dir 'session.v4.jsonl'), $lines)
}
$genSw.Stop()
$fxBytes = (Get-ChildItem $FixtureRoot -Recurse -File | Measure-Object Length -Sum).Sum
$fxFiles = (Get-ChildItem $FixtureRoot -Recurse -File).Count

# ---- 子进程扫描计时（独立进程以测峰值工作集）----
$harness = Join-Path $repo '.artifacts/tmp/perf-harness.ps1'
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
$ct = [System.Threading.CancellationToken]::None
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$res = $svc.Scan($Root, $ct)
$sw.Stop()
$scanMs = $sw.Elapsed.TotalMilliseconds
$dates = @($res.Sessions | Where-Object { $null -ne $_.CreatedAt } | ForEach-Object { $_.CreatedAt.UtcDateTime })
$minD = ($dates | Measure-Object -Minimum).Minimum
$maxD = ($dates | Measure-Object -Maximum).Maximum
$df = [DeepSeekHarnessCleaner.Application.DateFilter]::new(
    [DeepSeekHarnessCleaner.Application.DateFilterField]::CreatedAt,
    [DateOnly]::new($minD.Year, $minD.Month, $minD.Day),
    [DateOnly]::new($maxD.Year, $maxD.Month, $maxD.Day))
$req = [DeepSeekHarnessCleaner.Application.FilterRequest]::new($df, $null, $null, 'perf-1')
$tz = [TimeZoneInfo]::Utc
$fsw = [System.Diagnostics.Stopwatch]::StartNew()
$hits = $null
for ($n = 0; $n -lt 10; $n++) { $hits = [DeepSeekHarnessCleaner.Application.SessionFilter]::Apply($res.Sessions, $req, $tz) }
$fsw.Stop()
$out = [ordered]@{
    scanMs = [math]::Round($scanMs, 1)
    sessions = $res.Sessions.Count
    totalBytes = $res.TotalBytes
    anomalies = $res.AnomalousFiles.Count
    filterAvgMs = [math]::Round($fsw.Elapsed.TotalMilliseconds / 10, 2)
    filterHits = $hits.Count
    peakWSBytes = [System.Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64
}
$out | ConvertTo-Json -Compress
'@ | Set-Content -Path $harness -Encoding utf8

$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
$json = & pwsh -NoProfile -File $harness -Root $FixtureRoot -Bin $bin | Select-Object -Last 1
$sw2.Stop()
if ($LASTEXITCODE -ne 0) { throw "harness 子进程退出码 $LASTEXITCODE" }
$m = $json | ConvertFrom-Json

# ---- 门槛判定 ----
$checks = [ordered]@{
    'PERF-01 初扫 ≤15000ms'      = ($m.scanMs -le 15000)
    'PERF-01 筛选均值 ≤200ms'    = ($m.filterAvgMs -le 200)
    'PERF-01 峰值工作集 <512MiB' = ($m.peakWSBytes -lt 512MB)
    'PERF-01 fixture ≤200MB'     = ($fxBytes -le 200MB)
    '会话数 = 1000'              = ($m.sessions -eq $SessionCount)
    'fixture 会话数一致'          = ($fxFiles -eq $SessionCount)
    '筛选命中 1（零填充下 perf-1 仅匹配 perf-1000）' = ($m.filterHits -eq 1)
}
$allPass = -not ($checks.Values -contains $false)

$lines = @(
    "PERF-01 计时结果（T-33） $([DateTime]::UtcNow.ToString('s'))Z"
    $osLine; $cpuLine; "DISK=$diskLine"
    "FIXTURE=$FixtureRoot files=$fxFiles bytes=$fxBytes genSec=$([math]::Round($genSw.Elapsed.TotalSeconds,1))"
    "scanMs=$($m.scanMs) sessions=$($m.sessions) totalBytes=$($m.totalBytes) anomalies=$($m.anomalies)"
    "filterAvgMs=$($m.filterAvgMs) filterHits=$($m.filterHits) harnessTotalSec=$([math]::Round($sw2.Elapsed.TotalSeconds,1))"
    "peakWSMiB=$([math]::Round($m.peakWSBytes / 1MB, 1))"
)
foreach ($k in $checks.Keys) {
    $lines += ('{0} : {1}' -f $(if ($checks[$k]) { 'PASS' } else { 'FAIL' }), $k)
}
$lines += "PERF_RESULT=$(if ($allPass) { 'PASS' } else { 'FAIL' })"
$lines | Set-Content -Path $EvidencePath -Encoding utf8
$lines | ForEach-Object { Write-Output $_ }
if (-not $allPass) { exit 1 }
