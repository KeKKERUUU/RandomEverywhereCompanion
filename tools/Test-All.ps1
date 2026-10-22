#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [string]$TemporaryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$previousTemp = $env:TEMP
$previousTmp = $env:TMP

function Invoke-CheckedSuite {
    param([string]$Executable, [string[]]$Arguments, [string]$CompletionPattern)
    $output = @(& $Executable @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Output $_ }
    if ($exitCode -ne 0) { throw "Test process failed with exit code $exitCode." }
    if (-not ($output | Select-String -Pattern $CompletionPattern)) {
        throw 'Test process exited without its completion report; refusing a false-green result.'
    }
}

Push-Location $root
try {
    if ($TemporaryRoot) {
        $temp = [IO.Path]::GetFullPath($TemporaryRoot)
        [IO.Directory]::CreateDirectory($temp) | Out-Null
        $env:TEMP = $temp
        $env:TMP = $temp
    }
    # These projects have executable harnesses, not Microsoft.NET.Test.Sdk adapters.
    & $DotNetPath build RandomEverywhereInstaller.slnx -c $Configuration -m:1 --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
    $suiteFailures = [Collections.Generic.List[string]]::new()
    $suites = @(
        @{ Name = 'Core / installer'; Executable = $DotNetPath; Arguments = @("tests/RandomEverywhereInstaller.Tests/bin/$Configuration/net10.0/RandomEverywhereInstaller.Tests.dll"); CompletionPattern = '^All [1-9][0-9]* tests passed\.$' },
        @{ Name = 'Runtime / audio'; Executable = "./tests/CompanionRuntime.Tests/bin/$Configuration/net48/CompanionRuntime.Tests.exe"; Arguments = @(); CompletionPattern = '^All [1-9][0-9]* runtime/audio checks passed\.' },
        @{ Name = 'Headless WPF'; Executable = $DotNetPath; Arguments = @("tests/RandomEverywhereInstaller.App.Tests/bin/$Configuration/net10.0-windows/RandomEverywhereInstaller.App.Tests.dll"); CompletionPattern = '^[1-9][0-9]*/[1-9][0-9]* passed; 0 failed\.' }
    )
    foreach ($suite in $suites) {
        try {
            Invoke-CheckedSuite -Executable $suite.Executable -Arguments $suite.Arguments -CompletionPattern $suite.CompletionPattern
        }
        catch {
            $suiteFailures.Add("$($suite.Name): $($_.Exception.Message)")
            Write-Output "FAILED SUITE: $($suite.Name): $($_.Exception.Message)"
        }
    }
    if ($suiteFailures.Count -ne 0) {
        throw ($suiteFailures -join [Environment]::NewLine)
    }
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Pop-Location
}
