#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$SourcePath,

    [string]$OutputPath = ".\ZipDiagnostics",

    [switch]$Recurse
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$catalog = @(
    [pscustomobject]@{ Id="FR_VEHICLES"; Name="French Emergency Vehicles"; Required=$true; Sim="Both"; Terms=@("pack","vehicles"); Any=@("fr","french","france") },
    [pscustomobject]@{ Id="30WEST_HEMS"; Name="30West HEMS Objects"; Required=$true; Sim="Both"; Terms=@("30west","hems","objects"); Any=@() },
    [pscustomobject]@{ Id="30WEST_SMOKE"; Name="30West Smoke"; Required=$false; Sim="MSFS2024"; Terms=@("30west","smoke"); Any=@() },
    [pscustomobject]@{ Id="DE_VEHICLES"; Name="DE Vehicles"; Required=$false; Sim="Both"; Terms=@("action","pack","vehicles"); Any=@("de","german","germany") },
    [pscustomobject]@{ Id="IT_VEHICLES"; Name="IT Vehicles"; Required=$false; Sim="Both"; Terms=@("action","pack","vehicles"); Any=@("it","italian","italy") },
    [pscustomobject]@{ Id="US_VEHICLES"; Name="USA Vehicles"; Required=$false; Sim="Both"; Terms=@("action","pack","vehicles"); Any=@("us","usa","american") },
    [pscustomobject]@{ Id="NL_VEHICLES"; Name="NL Vehicles"; Required=$false; Sim="Both"; Terms=@("action","pack","vehicles"); Any=@("nl","dutch","netherlands") },
    [pscustomobject]@{ Id="UK_VEHICLES"; Name="UK Vehicles"; Required=$false; Sim="Both"; Terms=@("action","pack","vehicles"); Any=@("uk","british") },
    [pscustomobject]@{ Id="OFFSHORE_OBJECTS"; Name="Offshore Objects"; Required=$false; Sim="Both"; Terms=@("offshore","objects","pack"); Any=@() },
    [pscustomobject]@{ Id="ANDREW_MISSION_PACK"; Name="Andrew Mission Pack"; Required=$true; Sim="Both"; Terms=@("h145","andrew","mission","pack"); Any=@() },
    [pscustomobject]@{ Id="BUNDLE_VOICE_PACK"; Name="Bundle Voice Pack"; Required=$false; Sim="Both"; Terms=@("bundle","voice","pack"); Any=@() },
    [pscustomobject]@{ Id="DUSTIN_VOICE_PACK"; Name="Dustin Voice Pack"; Required=$false; Sim="Both"; Terms=@("h145","dustin","voice","pack"); Any=@() },
    [pscustomobject]@{ Id="H145_HEMS_OBJECTS"; Name="68ponyGT HEMS Objects"; Required=$true; Sim="Both"; Terms=@("hems","objects"); Any=@("h145","68ponygt") }
)

function Get-NormalizedName {
    param([string]$Name)
    $stem = [IO.Path]::GetFileNameWithoutExtension($Name).ToLowerInvariant()
    $normalized = ([regex]::Replace($stem, "[^a-z0-9]+", " ")).Trim()
    [pscustomobject]@{
        Normalized = $normalized
        Compact = $normalized.Replace(" ", "")
        Tokens = @($normalized.Split(@(" "), [StringSplitOptions]::RemoveEmptyEntries))
    }
}

function Find-PackageMatch {
    param([string]$Name)
    $candidate = Get-NormalizedName $Name
    foreach ($rule in $catalog) {
        $allTerms = $true
        foreach ($term in $rule.Terms) {
            if (-not $candidate.Compact.Contains($term)) { $allTerms = $false; break }
        }
        if (-not $allTerms) { continue }
        if ($rule.Any.Count -gt 0) {
            $anyTerm = $false
            foreach ($term in $rule.Any) {
                if (($candidate.Tokens -contains $term) -or $candidate.Compact.Contains($term)) { $anyTerm = $true; break }
            }
            if (-not $anyTerm) { continue }
        }
        $rule
    }
}

function Get-CommonDirectory {
    param([string[]]$Paths)
    $parts = @($Paths | ForEach-Object {
        $segments = @($_.Replace("\", "/").Trim("/").Split("/"))
        if ($segments.Count -gt 1) { ,$segments[0..($segments.Count - 2)] }
    })
    if ($parts.Count -eq 0) { return "" }
    $limit = ($parts | ForEach-Object Count | Measure-Object -Minimum).Minimum
    $common = New-Object Collections.Generic.List[string]
    for ($i=0; $i -lt $limit; $i++) {
        $value = $parts[0][$i]
        if (@($parts | Where-Object { $_[$i] -ine $value }).Count -gt 0) { break }
        $common.Add($value)
    }
    $common -join "/"
}

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportRoot = Join-Path (New-Item -ItemType Directory -Force -Path $OutputPath).FullName "ZipDebug-$stamp"
New-Item -ItemType Directory -Force -Path $reportRoot | Out-Null
$entryRoot = Join-Path $reportRoot "ArchiveEntries"
New-Item -ItemType Directory -Force -Path $entryRoot | Out-Null

$search = if ($Recurse) { [IO.SearchOption]::AllDirectories } else { [IO.SearchOption]::TopDirectoryOnly }
$zipFiles = @([IO.Directory]::EnumerateFiles($source, "*.zip", $search) | Sort-Object)
$rarFiles = @([IO.Directory]::EnumerateFiles($source, "*.rar", $search) | Sort-Object)
$results = New-Object Collections.Generic.List[object]
$errors = New-Object Collections.Generic.List[object]

Get-ChildItem -LiteralPath $source -Force -Recurse:$Recurse |
    Select-Object FullName, Name, PSIsContainer, Length, LastWriteTime |
    Format-Table -AutoSize | Out-File (Join-Path $reportRoot "SourceStructure.txt") -Encoding UTF8 -Width 4096

foreach ($zipPath in $zipFiles) {
    $file = Get-Item -LiteralPath $zipPath
    $safeName = [regex]::Replace($file.BaseName, "[^a-zA-Z0-9._-]", "_")
    $entryCsv = Join-Path $entryRoot "$safeName-entries.csv"
    $archive = $null
    try {
        $matches = @(Find-PackageMatch $file.Name)
        $archive = [IO.Compression.ZipFile]::OpenRead($file.FullName)
        $entries = @($archive.Entries | ForEach-Object {
            $path = $_.FullName.Replace("\", "/").TrimStart("/")
            [pscustomobject]@{
                Path = $path
                IsDirectory = [string]::IsNullOrEmpty($_.Name)
                CompressedBytes = $_.CompressedLength
                UncompressedBytes = $_.Length
                Ratio = if ($_.CompressedLength -gt 0) { [math]::Round($_.Length / $_.CompressedLength, 2) } else { 0 }
                TraversalRisk = ($path.Split("/") -contains "..") -or [IO.Path]::IsPathRooted($_.FullName)
            }
        })
        $entries | Export-Csv -LiteralPath $entryCsv -NoTypeInformation -Encoding UTF8
        $filePaths = @($entries | Where-Object { -not $_.IsDirectory } | Select-Object -ExpandProperty Path)
        $manifestRoots = @($filePaths | Where-Object { $_ -match "(^|/)manifest\.json$" } | ForEach-Object { Split-Path $_ -Parent })
        $layoutRoots = @($filePaths | Where-Object { $_ -match "(^|/)layout\.json$" } | ForEach-Object { Split-Path $_ -Parent })
        $topFolders = @($filePaths | ForEach-Object { ($_.Split("/"))[0] } | Sort-Object -Unique)
        $commonRoot = Get-CommonDirectory $filePaths
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash

        $results.Add([pscustomobject]@{
            FileName = $file.Name
            FullPath = $file.FullName
            ParentFolder = $file.DirectoryName
            SizeBytes = $file.Length
            SizeMB = [math]::Round($file.Length / 1MB, 2)
            SHA256 = $hash
            MatchCount = $matches.Count
            PackageIds = (($matches | ForEach-Object { $_.Id }) -join ",")
            PackageNames = (($matches | ForEach-Object { $_.Name }) -join ",")
            Required = (($matches | ForEach-Object { $_.Required }) -join ",")
            EntryCount = $entries.Count
            UncompressedBytes = ($entries | Measure-Object UncompressedBytes -Sum).Sum
            TopFolders = ($topFolders -join ",")
            CommonRoot = $commonRoot
            ManifestRoots = ($manifestRoots -join ",")
            LayoutRoots = ($layoutRoots -join ",")
            TraversalRisks = @($entries | Where-Object TraversalRisk).Count
            EntryReport = $entryCsv
            Error = ""
        })
    }
    catch {
        $errors.Add([pscustomobject]@{ File=$file.FullName; Type=$_.Exception.GetType().FullName; Message=$_.Exception.Message })
        $results.Add([pscustomobject]@{ FileName=$file.Name; FullPath=$file.FullName; ParentFolder=$file.DirectoryName; SizeBytes=$file.Length; SizeMB=[math]::Round($file.Length/1MB,2); SHA256=""; MatchCount=0; PackageIds=""; PackageNames=""; Required=""; EntryCount=0; UncompressedBytes=0; TopFolders=""; CommonRoot=""; ManifestRoots=""; LayoutRoots=""; TraversalRisks=0; EntryReport=""; Error=$_.Exception.Message })
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
    }
}

foreach ($rarPath in $rarFiles) {
    $file = Get-Item -LiteralPath $rarPath
    $matches = @(Find-PackageMatch $file.Name)
    $results.Add([pscustomobject]@{
        FileName=$file.Name; FullPath=$file.FullName; ParentFolder=$file.DirectoryName
        SizeBytes=$file.Length; SizeMB=[math]::Round($file.Length/1MB,2); SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        MatchCount=$matches.Count
        PackageIds=(($matches | ForEach-Object { $_.Id }) -join ",")
        PackageNames=(($matches | ForEach-Object { $_.Name }) -join ",")
        Required=(($matches | ForEach-Object { $_.Required }) -join ",")
        EntryCount=0; UncompressedBytes=0; TopFolders=""; CommonRoot=""; ManifestRoots=""; LayoutRoots=""; TraversalRisks=0; EntryReport=""
        Error="RAR archive detected. System.IO.Compression can inspect ZIP files only."
    })
}

$results | Export-Csv -LiteralPath (Join-Path $reportRoot "ZipSummary.csv") -NoTypeInformation -Encoding UTF8
$results | ConvertTo-Json -Depth 8 | Out-File (Join-Path $reportRoot "ZipSummary.json") -Encoding UTF8
$errors | ConvertTo-Json -Depth 5 | Out-File (Join-Path $reportRoot "Errors.json") -Encoding UTF8

@(
    "Random & Everywhere Installer - ZIP diagnostic"
    "Generated: $(Get-Date -Format o)"
    "Source: $source"
    "Recursive: $($Recurse.IsPresent)"
    "ZIP count: $($zipFiles.Count)"
    "RAR count: $($rarFiles.Count)"
    "Report: $reportRoot"
    ""
    ($results | Format-Table FileName,SizeMB,MatchCount,PackageIds,Required,EntryCount,CommonRoot,Error -AutoSize | Out-String -Width 4096)
) | Out-File (Join-Path $reportRoot "Summary.txt") -Encoding UTF8 -Width 4096

Write-Host "Diagnostic completed." -ForegroundColor Green
Write-Host "Report folder: $reportRoot"
