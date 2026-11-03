<#
.SYNOPSIS
    Reads a data disc into a 2048-byte-sector image, zero-filling sectors the drive cannot read.

.DESCRIPTION
    Reads the drive as a raw volume, so the disc needs a filesystem Windows can mount (an ISO 9660
    or UDF disc: a CD data disc, or a DVD generated with --data-dir). Run it from an elevated
    PowerShell. The image starts at LBA 0, which is what `extract` and `cd-extract` expect.

    The number of sectors comes from the ISO 9660 volume size (sector 16) unless -Sectors is given.
    A DVD generated without --data-dir has no filesystem and cannot be read this way: read it on
    Linux with read-disc.sh.

.EXAMPLE
    .\read-disc.ps1 -Drive E -Output C:\temp\readback.iso
#>
param(
    [Parameter(Mandatory = $true)][string]$Drive,
    [Parameter(Mandatory = $true)][string]$Output,
    [long]$Sectors = 0,
    [int]$ChunkSectors = 32
)

$ErrorActionPreference = 'Stop'
$sectorBytes = 2048
$letter = $Drive.Substring(0, 1).ToUpperInvariant()
$devicePath = '\\.\' + $letter + ':'

function Read-Fully([System.IO.Stream]$stream, [byte[]]$buffer, [int]$count) {
    $total = 0
    while ($total -lt $count) {
        $read = $stream.Read($buffer, $total, $count - $total)
        if ($read -le 0) { break }
        $total += $read
    }
    return $total
}

# A buffer size of 1 turns off FileStream's own buffering: raw volume reads must be whole sectors.
$source = New-Object System.IO.FileStream($devicePath, [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite, 1)
try {
    if ($Sectors -le 0) {
        $header = New-Object byte[] $sectorBytes
        $null = $source.Seek(16L * $sectorBytes, [System.IO.SeekOrigin]::Begin)
        $got = Read-Fully $source $header $sectorBytes
        $magic = [System.Text.Encoding]::ASCII.GetString($header, 1, 5)
        if ($got -eq $sectorBytes -and $magic -eq 'CD001') {
            $Sectors = [BitConverter]::ToUInt32($header, 80)
            Write-Host "volume size from the ISO 9660 header: $Sectors sectors"
        } else {
            throw "No ISO 9660 header found. Pass -Sectors, or read the disc with read-disc.sh."
        }
    }

    $target = New-Object System.IO.FileStream($Output, [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 1MB)
    try {
        $buffer = New-Object byte[] ($ChunkSectors * $sectorBytes)
        $zeros = New-Object byte[] $sectorBytes
        $unreadable = New-Object System.Collections.Generic.List[long]
        $lba = 0L
        while ($lba -lt $Sectors) {
            $count = [int][Math]::Min($ChunkSectors, $Sectors - $lba)
            $okChunk = $false
            try {
                $null = $source.Seek($lba * $sectorBytes, [System.IO.SeekOrigin]::Begin)
                $got = Read-Fully $source $buffer ($count * $sectorBytes)
                if ($got -eq $count * $sectorBytes) {
                    $target.Write($buffer, 0, $got)
                    $okChunk = $true
                }
            } catch [System.IO.IOException] { $okChunk = $false }

            if (-not $okChunk) {
                # Fall back to one sector at a time so a bad sector costs only itself.
                for ($index = 0; $index -lt $count; $index++) {
                    $one = $lba + $index
                    $good = $false
                    try {
                        $null = $source.Seek($one * $sectorBytes, [System.IO.SeekOrigin]::Begin)
                        $got = Read-Fully $source $buffer $sectorBytes
                        if ($got -eq $sectorBytes) {
                            $target.Write($buffer, 0, $sectorBytes)
                            $good = $true
                        }
                    } catch [System.IO.IOException] { $good = $false }
                    if (-not $good) {
                        $target.Write($zeros, 0, $sectorBytes)
                        $unreadable.Add($one)
                    }
                }
            }

            $lba += $count
            if (($lba % 4096) -lt $count) {
                Write-Progress -Activity "Reading $devicePath" -Status "$lba / $Sectors sectors" `
                    -PercentComplete ([int](100 * $lba / $Sectors))
            }
        }
    }
    finally { $target.Dispose() }
}
finally { $source.Dispose() }

Write-Progress -Activity "Reading $devicePath" -Completed
Write-Host "wrote $Sectors sectors to $Output"
if ($unreadable.Count -eq 0) {
    Write-Host 'every sector was read'
} else {
    Write-Warning "$($unreadable.Count) sectors could not be read and were zero-filled; first: $($unreadable[0]), last: $($unreadable[$unreadable.Count - 1])"
}
Write-Host "next: disc-image-studio compare --expected generated.iso --actual $Output"
