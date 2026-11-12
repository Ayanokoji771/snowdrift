# Snowdrift for Windows: install or update with one command (PowerShell):
#
#   irm https://raw.githubusercontent.com/Ayanokoji771/snowdrift/main/install.ps1 | iex
#
# Downloads the latest release from GitHub, checks it against the release's SHA-256 checksums,
# puts Snowdrift.exe where it already lives (or in %LOCALAPPDATA%\Programs\Snowdrift), adds it to
# the Start menu and starts it. Settings are kept. No administrator rights needed.
# SNOWDRIFT_DIR=folder installs there; SNOWDRIFT_NO_START=1 skips starting it.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo = 'Ayanokoji771/snowdrift'
    $ru = (Get-UICulture).TwoLetterISOLanguageName -eq 'ru'
    function Say([string]$r, [string]$e) { if ($ru) { Write-Host $r } else { Write-Host $e } }

    # curl.exe (built into Windows 10 and 11) is fast and shows a progress bar; .NET otherwise.
    function Get-File([string]$url, [string]$out, [switch]$Quiet) {
        $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
        if (Test-Path $curl) {
            $ErrorActionPreference = 'Continue'
            $bar = if ($Quiet) { '-s' } else { '--progress-bar' }
            & $curl -fL $bar --retry 3 --retry-delay 2 --connect-timeout 20 -o $out $url
            if ($LASTEXITCODE -eq 0) { return }
            $ErrorActionPreference = 'Stop'
        }
        (New-Object Net.WebClient).DownloadFile($url, $out)
    }

    # The native architecture, also when this PowerShell runs emulated on ARM.
    $arch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
    $asset = if ($arch -eq 'ARM64') { 'Snowdrift-Windows-ARM64.zip' } else { 'Snowdrift-Windows-x64.zip' }

    # Where: the running copy, the copy that starts with Windows, or the default folder.
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $auto = (Get-ItemProperty -Path $runKey -Name Snowdrift -ErrorAction SilentlyContinue).Snowdrift
    if ($auto) { $auto = $auto.Trim('"') }
    $running = Get-Process -Name Snowdrift -ErrorAction SilentlyContinue | Where-Object { $_.Path } | Select-Object -First 1
    if ($env:SNOWDRIFT_DIR) { $target = Join-Path $env:SNOWDRIFT_DIR 'Snowdrift.exe' }
    elseif ($running) { $target = $running.Path }
    elseif ($auto -and (Test-Path $auto)) { $target = $auto }
    else { $target = Join-Path $env:LOCALAPPDATA 'Programs\Snowdrift\Snowdrift.exe' }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('snowdrift-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $files = "https://github.com/$repo/releases/latest/download"
        Say "Скачиваю Snowdrift ($asset, около 70 МБ: внутри всё нужное для работы, ставить ничего больше не надо)..." `
            "Downloading Snowdrift ($asset, about 70 MB: everything it needs is inside)..."
        $zip = Join-Path $tmp $asset
        $started = Get-Date
        Get-File "$files/$asset" $zip
        Get-File "$files/SHA256SUMS.txt" (Join-Path $tmp 'SHA256SUMS.txt') -Quiet
        $mb = [math]::Round((Get-Item $zip).Length / 1MB)
        $sec = [math]::Max(1, [math]::Round(((Get-Date) - $started).TotalSeconds))
        Say "Скачано: $mb МБ за $sec с. Проверяю контрольную сумму..." "Downloaded $mb MB in $sec s. Checking the checksum..."

        $line = Get-Content (Join-Path $tmp 'SHA256SUMS.txt') | Where-Object { ($_ -split '\s+')[-1].TrimStart('*') -eq $asset } | Select-Object -First 1
        $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash
        if (-not $line -or ($line -split '\s+')[0] -ne $actual) {
            Say 'Контрольная сумма не совпала — ничего не изменено.' 'Checksum mismatch — nothing was changed.'
            return
        }
        Say 'Контрольная сумма совпала. Распаковываю...' 'Checksum verified. Unpacking...'

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, (Join-Path $tmp 'new'))
        $fresh = Join-Path $tmp 'new\Snowdrift.exe'
        $version = ((Get-Item $fresh).VersionInfo.ProductVersion -split '\+')[0]

        # Close the copy being replaced (only that one).
        Get-Process -Name Snowdrift -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target } | ForEach-Object {
            Say 'Закрываю запущенный Snowdrift...' 'Closing the running Snowdrift...'
            $_ | Stop-Process -Force
            $_.WaitForExit(5000) | Out-Null
        }

        Say "Устанавливаю в $target..." "Installing to $target..."
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        for ($i = 0; ; $i++) {
            try { Copy-Item -Path $fresh -Destination $target -Force; break }
            catch { if ($i -ge 10) { throw }; Start-Sleep -Milliseconds 500 }
        }
        Unblock-File -Path $target -ErrorAction SilentlyContinue
        Remove-Item "$target.old" -Force -ErrorAction SilentlyContinue

        # Keep "Start with Windows" pointing at this copy, and add a Start menu entry.
        if ($auto -and $auto -ne $target) { Set-ItemProperty -Path $runKey -Name Snowdrift -Value "`"$target`"" }
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Snowdrift.lnk'))
        $link.TargetPath = $target
        $link.WorkingDirectory = Split-Path $target
        $link.Save()

        if (-not $env:SNOWDRIFT_NO_START) {
            Say 'Запускаю...' 'Starting...'
            Start-Process -FilePath $target
        }
        Say "Готово: Snowdrift $version — $target" "Done: Snowdrift $version — $target"
        Say 'Снежинка — в трее справа внизу. Дальше Snowdrift обновляется сам, или запустите эту команду ещё раз.' 'The snowflake is in the tray. Snowdrift updates itself from now on, or run this command again.'
    }
    finally {
        Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
