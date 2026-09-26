<#
.SYNOPSIS
    Builds and uploads the wk_hid firmware to an Arduino Pro Micro / Leonardo.

.DESCRIPTION
    Everything needed is fetched into arduino\.toolchain, which is git-ignored: a portable
    arduino-cli and the arduino:avr core. Nothing is installed onto the machine and nothing
    outside this folder is touched, so removing that folder undoes the whole thing.

    The upload is the fiddly part, and it is fiddly for a reason worth knowing. A board running
    wk_hid is a HID device with a CDC port bolted on; it is not listening for a new sketch. The
    way in is the "1200 baud touch": opening its serial port at exactly 1200 baud and dropping DTR
    tells the 32U4 to reset into its bootloader, which appears as a DIFFERENT COM port for about
    eight seconds. This script does the touch, waits for that new port to appear, and uploads to
    it. (That is also why ArduinoLink never opens a port at 1200 baud — it would knock the board
    out of being a keyboard mid-hunt.)

    If the board is brand new and has never been flashed, there is nothing to touch: it is already
    running the factory sketch. Pass -Port with whatever COM port Windows shows, or press the
    board's RST pin to ground twice quickly to force the bootloader and run this within eight
    seconds.

.PARAMETER Port
    COM port of the board. Omit to auto-detect: the only port present, or the one already running
    wk_hid.

.PARAMETER Fqbn
    Board identifier. Defaults to the Pro Micro at 5V/16MHz — SparkFun's board, which is what the
    common "Arduino Pro Micro ATmega32U4" listings are. Use arduino:avr:leonardo for a genuine
    Leonardo, or arduino:avr:micro for an official Arduino Micro.

.PARAMETER CompileOnly
    Build and stop. Useful for checking the sketch compiles with no board plugged in.

.EXAMPLE
    .\flash.ps1
.EXAMPLE
    .\flash.ps1 -Port COM7 -Fqbn arduino:avr:leonardo
.EXAMPLE
    .\flash.ps1 -CompileOnly
#>
[CmdletBinding()]
param(
    [string] $Port,
    [string] $Fqbn = 'SparkFun:avr:promicro:cpu=16MHzatmega32U4',
    [switch] $CompileOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolchain = Join-Path $root '.toolchain'
$cliExe    = Join-Path $toolchain 'arduino-cli.exe'
$dataDir   = Join-Path $toolchain 'data'
$sketch    = Join-Path $root 'wk_hid'

# SparkFun's boards are not in the built-in index, so the Pro Micro needs its package URL added.
# A genuine Leonardo or Micro does not, but adding it costs one download and keeps the default
# FQBN above working out of the box.
$sparkfunIndex = 'https://raw.githubusercontent.com/sparkfun/Arduino_Boards/master/IDE_Board_Manager/package_sparkfun_index.json'

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Note([string] $text) { Write-Host "    $text" -ForegroundColor DarkGray }

function Invoke-Cli {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $CliArgs)
    & $cliExe --config-file (Join-Path $toolchain 'arduino-cli.yaml') @CliArgs
    if ($LASTEXITCODE -ne 0) { throw "arduino-cli failed: $($CliArgs -join ' ')" }
}

# ── 1. Toolchain ────────────────────────────────────────────────────────────────
if (-not (Test-Path $cliExe)) {
    Write-Step 'Downloading arduino-cli (portable, into arduino\.toolchain)'
    New-Item -ItemType Directory -Force -Path $toolchain | Out-Null
    $zip = Join-Path $toolchain 'arduino-cli.zip'
    $url = 'https://downloads.arduino.cc/arduino-cli/arduino-cli_latest_Windows_64bit.zip'
    # TLS 1.2 is not the default in Windows PowerShell 5.1 and the download host requires it.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $toolchain -Force
    Remove-Item $zip -Force
} else {
    Write-Note "arduino-cli already present in $toolchain"
}

# Keeping the config, the cores and the cache inside .toolchain is the whole point of "portable":
# without it arduino-cli writes into %LOCALAPPDATA%\Arduino15 and starts sharing state with any
# Arduino IDE the user may install later.
$configPath = Join-Path $toolchain 'arduino-cli.yaml'
if (-not (Test-Path $configPath)) {
    Write-Step 'Writing the portable arduino-cli config'
    $yaml = @"
directories:
  data: $dataDir
  downloads: $dataDir/staging
  user: $dataDir/user
board_manager:
  additional_urls:
    - $sparkfunIndex
"@
    # Written without a BOM on purpose: Set-Content -Encoding utf8 adds one in Windows
    # PowerShell 5.1, and arduino-cli's YAML parser reads it as part of the first key.
    [System.IO.File]::WriteAllText($configPath, $yaml, (New-Object System.Text.UTF8Encoding $false))
}

if (-not (Test-Path (Join-Path $dataDir 'packages\arduino\hardware\avr'))) {
    Write-Step 'Installing the AVR core (one download, a few hundred MB of toolchain)'
    Invoke-Cli core update-index
    Invoke-Cli core install arduino:avr
}

if ($Fqbn -like 'SparkFun:*' -and -not (Test-Path (Join-Path $dataDir 'packages\SparkFun'))) {
    Write-Step 'Installing the SparkFun AVR core (Pro Micro board definition)'
    Invoke-Cli core update-index
    Invoke-Cli core install SparkFun:avr
}

# ── 2. Compile ──────────────────────────────────────────────────────────────────
Write-Step "Compiling wk_hid for $Fqbn"
Invoke-Cli compile --fqbn $Fqbn $sketch

if ($CompileOnly) {
    Write-Host ''
    Write-Host 'Compiled. Nothing was uploaded (-CompileOnly).' -ForegroundColor Green
    exit 0
}

# ── 3. Find the board ───────────────────────────────────────────────────────────
if (-not $Port) {
    $ports = [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object
    if ($ports.Count -eq 0) {
        throw @'
No COM port found.

Check, in this order:
  1. The USB cable carries DATA. A charge-only cable enumerates nothing at all, and it is by
     far the most common cause of this.
  2. The board is a native-USB one (Pro Micro / Leonardo / Micro, ATmega32U4). A Nano or Uno
     cannot be a keyboard — its USB is a separate serial chip, not the microcontroller.
  3. Device Manager shows it. A board that is there but unrecognised appears under
     "Other devices" and needs its driver.
'@
    }
    if ($ports.Count -gt 1) {
        throw "Several COM ports present ($($ports -join ', ')). Pass -Port to say which is the board."
    }
    $Port = $ports[0]
    Write-Note "Using $Port (the only port present)"
}

# ── 4. Upload ───────────────────────────────────────────────────────────────────
#
# The board's APPLICATION port is what goes in, not the bootloader's. arduino-cli does the whole
# dance itself for a 32U4 — the board definition carries upload.use_1200bps_touch and
# upload.wait_for_upload_port, so it resets the board, waits for the bootloader's (different) port
# to enumerate and uploads there.
#
# This script used to do the touch by hand and hand avrdude the port it found. It worked exactly
# often enough to look right: the Caterina bootloader only stays up for about eight seconds, and
# between a 250 ms polling loop and arduino-cli's own start-up that window was routinely gone by
# the time avrdude said hello — which it reports as "butterfly_recv(pgm, &c, 1) failed", a message
# that reads like a broken board rather than a race.
Write-Step "Uploading to $Port (arduino-cli resets the board into its bootloader)"
Invoke-Cli upload --fqbn $Fqbn --port $Port $sketch

Write-Host ''
Write-Host 'Done. The board is now a keyboard, a mouse and a serial port.' -ForegroundColor Green
Write-Host 'In the bot: Setup -> Firmware -> Ask the board again.' -ForegroundColor Green
