param(
    [string]$VideoDir = "D:\VaderResearch\_internal\mdsk360player\mdsk_external\Videos",
    [string]$Ffmpeg = "D:\VaderResearch\_internal\mdsk360player\MDSK_360_VideoPlayer_Data\StreamingAssets\FFmpegOut\Windows\ffmpeg.exe",
    [string]$BackupDir = "$env:LOCALAPPDATA\Temp\MDSK_DecodeBench",
    [string]$WorkDir = "$env:LOCALAPPDATA\Temp\MDSK_HEVC_Batch",
    [string]$LogPath = "$env:LOCALAPPDATA\Temp\MDSK_HEVC_Batch\batch.log"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$xorKey = [byte[]](0x4a, 0x3b, 0x2c, 0x1d, 0xe2, 0xf3, 0xa4, 0xb5, 0x86, 0x97, 0x08, 0x19, 0xaa, 0xbb, 0xcc, 0xdd)

Add-Type -TypeDefinition @"
using System;
using System.IO;

public static class XorFileTransform
{
    public static void Copy(string source, string destination, byte[] key)
    {
        const int BufferSize = 4 * 1024 * 1024;
        byte[] buffer = new byte[BufferSize];
        long offset = 0;

        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    buffer[i] = (byte)(buffer[i] ^ key[(offset + i) % key.Length]);
                }

                output.Write(buffer, 0, read);
                offset += read;
            }
        }
    }
}
"@

function Write-Log {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    Write-Output $line
}

function Get-VideoCodec {
    param([string]$Path)
    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $output = & $Ffmpeg -hide_banner -i $Path 2>&1 | Out-String
    $ErrorActionPreference = $oldErrorActionPreference
    if ($output -match "Video:\s*hevc") { return "hevc" }
    if ($output -match "Video:\s*h264") { return "h264" }
    if ($output -match "Video:\s*([^,\s]+)") { return $matches[1] }
    return "unknown"
}

function Test-Mp4Header {
    param([string]$Path)
    $buffer = New-Object byte[] 32
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        [void]$stream.Read($buffer, 0, $buffer.Length)
    }
    finally {
        $stream.Dispose()
    }
    $header = [System.Text.Encoding]::ASCII.GetString($buffer)
    return $header.Contains("ftyp")
}

function Get-FileHashShort {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return "" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Substring(0, 16)
}

New-Item -ItemType Directory -Force -Path $BackupDir, $WorkDir | Out-Null
if (Test-Path -LiteralPath $LogPath) {
    Remove-Item -LiteralPath $LogPath -Force
}

Write-Log "Starting HEVC conversion batch."
Write-Log "VideoDir=$VideoDir"
Write-Log "BackupDir=$BackupDir"
Write-Log "WorkDir=$WorkDir"
Write-Log "Ffmpeg=$Ffmpeg"

$files = Get-ChildItem -LiteralPath $VideoDir -Filter *.mp4 |
    Where-Object { $_.Name -notlike "*_sample*" } |
    Sort-Object Name

Write-Log ("Found {0} encrypted production mp4 files excluding *_sample." -f $files.Count)

$index = 0
foreach ($file in $files) {
    $index++
    $id = [Guid]::NewGuid().ToString("N")
    $backupPath = Join-Path $BackupDir ("ORIGINAL_ENCRYPTED_{0}" -f $file.Name)
    $decryptedPath = Join-Path $WorkDir ("$id.decrypted.mp4")
    $hevcPlainPath = Join-Path $WorkDir ("$id.hevc.mp4")
    $encryptedNewPath = Join-Path $WorkDir ("$id.encrypted.mp4")

    Write-Log ("[{0}/{1}] Processing {2} ({3:N3} GB)." -f $index, $files.Count, $file.Name, ($file.Length / 1GB))
    Write-Log ("[{0}/{1}] Original encrypted hash {2}." -f $index, $files.Count, (Get-FileHashShort $file.FullName))

    try {
        if (-not (Test-Path -LiteralPath $backupPath)) {
            Copy-Item -LiteralPath $file.FullName -Destination $backupPath
            Write-Log ("[{0}/{1}] Backed up original encrypted file to {2}." -f $index, $files.Count, $backupPath)
        } else {
            Write-Log ("[{0}/{1}] Backup already exists, leaving it unchanged: {2}." -f $index, $files.Count, $backupPath)
        }

        $isPlainMp4 = Test-Mp4Header $file.FullName
        if ($isPlainMp4) {
            Write-Log ("[{0}/{1}] Source header is plaintext mp4; using it directly for transcode, then writing encrypted HEVC output." -f $index, $files.Count)
            Copy-Item -LiteralPath $file.FullName -Destination $decryptedPath
        } else {
            Write-Log ("[{0}/{1}] Source header is encrypted; decrypting for codec check/transcode." -f $index, $files.Count)
            [XorFileTransform]::Copy($file.FullName, $decryptedPath, $xorKey)
        }

        $codec = Get-VideoCodec $decryptedPath
        Write-Log ("[{0}/{1}] Decrypted codec={2}." -f $index, $files.Count, $codec)

        if ($codec -eq "hevc" -and -not $isPlainMp4) {
            Write-Log ("[{0}/{1}] Already encrypted HEVC, skipping transcode and replacement." -f $index, $files.Count)
            continue
        }

        if ($codec -eq "hevc" -and $isPlainMp4) {
            Write-Log ("[{0}/{1}] Already HEVC but plaintext; encrypting in place to match production encrypted-video behavior." -f $index, $files.Count)
            [XorFileTransform]::Copy($decryptedPath, $encryptedNewPath, $xorKey)
            Move-Item -LiteralPath $encryptedNewPath -Destination $file.FullName -Force
            continue
        }

        Write-Log ("[{0}/{1}] Transcoding to HEVC 6K with NVENC." -f $index, $files.Count)
        $oldErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        & $Ffmpeg -hide_banner -y `
            -i $decryptedPath `
            -map 0:v:0 -map 0:a? -map_metadata 0 `
            -c:v hevc_nvenc -preset default -profile:v main -pix_fmt yuv420p `
            -b:v 35M -maxrate 45M -bufsize 70M -tag:v hvc1 `
            -c:a copy -movflags +faststart `
            $hevcPlainPath 2>&1 | ForEach-Object { Write-Log ("[{0}/{1}] ffmpeg {2}" -f $index, $files.Count, $_) }
        $ffmpegExitCode = $LASTEXITCODE
        $ErrorActionPreference = $oldErrorActionPreference
        if ($ffmpegExitCode -ne 0) {
            throw "ffmpeg failed with exit code $ffmpegExitCode."
        }

        $newCodec = Get-VideoCodec $hevcPlainPath
        if ($newCodec -ne "hevc") {
            throw "Transcode output codec was $newCodec, expected hevc."
        }

        Write-Log ("[{0}/{1}] Encrypting HEVC output." -f $index, $files.Count)
        [XorFileTransform]::Copy($hevcPlainPath, $encryptedNewPath, $xorKey)

        Move-Item -LiteralPath $encryptedNewPath -Destination $file.FullName -Force
        $newFile = Get-Item -LiteralPath $file.FullName
        Write-Log ("[{0}/{1}] Replaced production file. New size {2:N3} GB, hash {3}." -f $index, $files.Count, ($newFile.Length / 1GB), (Get-FileHashShort $newFile.FullName))
    }
    catch {
        Write-Log ("[{0}/{1}] ERROR {2}: {3}" -f $index, $files.Count, $file.Name, $_.Exception.Message)
        throw
    }
    finally {
        foreach ($tmp in @($decryptedPath, $hevcPlainPath, $encryptedNewPath)) {
            if (Test-Path -LiteralPath $tmp) {
                Remove-Item -LiteralPath $tmp -Force
            }
        }
        $freeD = [math]::Round((Get-PSDrive D).Free / 1GB, 2)
        $freeC = [math]::Round((Get-PSDrive C).Free / 1GB, 2)
        Write-Log ("[{0}/{1}] Free space: C={2} GB, D={3} GB." -f $index, $files.Count, $freeC, $freeD)
    }
}

Write-Log "HEVC conversion batch finished."
