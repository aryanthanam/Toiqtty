param(
    [Parameter(Mandatory)][string[]]$BaselineReports,
    [Parameter(Mandatory)][string[]]$FinalReports,
    [string]$Output='artifacts/performance-comparison.json',
    [string]$Context='Same machine and benchmark version; inspect throughput and background load before interpreting differences.',
    [double]$QuietP95TargetMs=8.33,
    [double]$CpuReductionTargetPercent=25,
    [double]$AllocationReductionTargetPercent=50
)
$ErrorActionPreference='Stop'
function Read-Reports([string[]]$paths){
    foreach($path in $paths){
        $report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if($report.benchmarkVersion -notin 2,3){throw "Only event-based benchmark versions 2 and 3 are supported: $path"}
        if($report.benchmarkVersion -ne $script:benchmarkVersion){throw "Cannot compare different benchmark versions: $path"}
        $keys=@{}
        foreach($measurement in $report.measurements){
            if($measurement.phase -eq 'idle'){continue}
            $key="$($measurement.paneCount):$($measurement.phase)"
            if($keys.ContainsKey($key)){throw "Duplicate measurement $key in $path"}
            $keys[$key]=$true
            if($measurement.seconds -le 0 -or @($measurement.samples).Count -eq 0){throw "Incomplete measurement $key in $path"}
            foreach($sample in $measurement.samples){
                if($null -eq $sample.PresentMs -or ![double]::IsFinite([double]$sample.PresentMs) -or $sample.PresentMs -lt 0){throw "Invalid latency in $key in $path"}
            }
            $measurement
        }
        if($null -eq $script:expectedKeys){$script:expectedKeys=@($keys.Keys | Sort-Object)}
        elseif(@(Compare-Object $script:expectedKeys @($keys.Keys | Sort-Object)).Count){throw "Measurement cases differ across reports: $path"}
    }
}
function Percentile([double[]]$values,[double]$fraction){
    if(!$values.Count){throw 'No latency samples.'}
    $sorted=@($values | Sort-Object); $sorted[[int][Math]::Floor(($sorted.Count-1)*$fraction)]
}
function Aggregate($cases){
    if(!$cases.Count){throw 'No matching measurements.'}
    $seconds=($cases.seconds | Measure-Object -Sum).Sum
    $samples=@($cases.samples | ForEach-Object { $_ })
    [pscustomobject]@{
        runs=$cases.Count;seconds=$seconds;cpuCores=($cases.uiCpuMs | Measure-Object -Sum).Sum/$seconds/1000
        hostCpuCores=($cases.hostCpuMs | Measure-Object -Sum).Sum/$seconds/1000
        allocatedMiBPerSecond=($cases.uiAllocatedBytes | Measure-Object -Sum).Sum/$seconds/1MB
        outputMiBPerSecond=($cases.bytesRead | Measure-Object -Sum).Sum/$seconds/1MB
        medianMs=Percentile @($samples.PresentMs) .5;p95Ms=Percentile @($samples.PresentMs) .95
        p99Ms=Percentile @($samples.PresentMs) .99;maxMs=($samples.PresentMs | Measure-Object -Maximum).Maximum
        samples=$samples.Count
    }
}
if(!$BaselineReports.Count -or !$FinalReports.Count){throw 'Both baseline and final reports are required.'}
$script:benchmarkVersion=(Get-Content -LiteralPath $BaselineReports[0] -Raw | ConvertFrom-Json).benchmarkVersion
$script:expectedKeys=$null
$baseline=@(Read-Reports $BaselineReports); $final=@(Read-Reports $FinalReports); $comparisons=@()
foreach($count in @($baseline.paneCount | Sort-Object -Unique)){foreach($phase in @($baseline | Where-Object paneCount -eq $count | Select-Object -ExpandProperty phase -Unique)){
    $before=Aggregate @($baseline | Where-Object { $_.paneCount -eq $count -and $_.phase -eq $phase })
    $after=Aggregate @($final | Where-Object { $_.paneCount -eq $count -and $_.phase -eq $phase })
    $cpu=if($before.cpuCores -gt 0){100*(1-$after.cpuCores/$before.cpuCores)}else{$null}
    $allocation=if($before.allocatedMiBPerSecond -gt 0){100*(1-$after.allocatedMiBPerSecond/$before.allocatedMiBPerSecond)}else{$null}
    $comparisons+= [pscustomobject]@{phase=$phase;paneCount=$count;baseline=$before;final=$after;cpuReductionPercent=$cpu;allocationReductionPercent=$allocation
        targetPassed=if($phase -eq 'quiet'){$after.p95Ms -le $QuietP95TargetMs}else{$null -ne $cpu -and $null -ne $allocation -and $cpu -ge $CpuReductionTargetPercent -and $allocation -ge $AllocationReductionTargetPercent}}
}}
$record=[ordered]@{timestamp=[DateTimeOffset]::Now.ToString('o');benchmarkVersion=$script:benchmarkVersion;context=$Context;baselineReports=$BaselineReports;finalReports=$FinalReports
    targets=@{quietP95Ms=$QuietP95TargetMs;cpuReductionPercent=$CpuReductionTargetPercent;allocationReductionPercent=$AllocationReductionTargetPercent}
    meaning='Latency percentiles pool per-sample input-admission to response-frame Present return. CPU and allocation rates pool total counters over total measured duration. Physical scanout is not measured. Saturated throughput can differ; controlled output is the matched-rate comparison.'
    allTargetsPassed=@($comparisons | Where-Object { !$_.targetPassed }).Count -eq 0;comparisons=$comparisons}
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output
$comparisons | Select-Object phase,paneCount,@{n='p95 ms';e={[Math]::Round($_.final.p95Ms,2)}},@{n='CPU reduction %';e={[Math]::Round($_.cpuReductionPercent,1)}},@{n='allocation reduction %';e={[Math]::Round($_.allocationReductionPercent,1)}},targetPassed | Format-Table
Write-Output "Comparison evidence: $Output"
