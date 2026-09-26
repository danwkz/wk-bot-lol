Imports System.Threading.Tasks
Imports wklol.Core

' ══════════════════════════════════════════════════════════════════════════════
' ArduinoSetup  —  the Setup screen: a board from "still in the bag" to pressing
' keys, one step at a time.
'
' THE ORDER IS NOT THE USER'S PROBLEM. Each step checks ONE thing, says plainly
' whether it passed, and the flow unlocks the next only when it did. A board with
' no firmware cannot reach the Check step at all: the only button offered is the
' one that would help. This is wk's Arduino wizard (FrmHardwareSetup), moved into
' the page; the checks and the words are the same.
'
' THE VERDICTS LIVE HERE, NOT IN THE PAGE. What a step found is kept for the
' session and handed back whole after every command, so the page renders it and
' decides nothing — a reload, or the Home card, reads the same answer.
'
' "COMPLETE" IS REMEMBERED; A STEP IS NOT. Walking the flow to its end sets
' InputConfig.ArduinoReady, which is what opens the rest of the interface on every
' later launch. A step that has not been run this session reads as passed when
' setup was completed before, and as whatever it found once it is run again.
' ══════════════════════════════════════════════════════════════════════════════

Public NotInheritable Class ArduinoSetup

    ''' <summary>What one step found, this session.</summary>
    Private NotInheritable Class StepResult
        Public Property Passed As Boolean
        ''' <summary>"ok", "warn", "danger" — or Nothing for a verdict that is not one yet.</summary>
        Public Property Tone As String
        Public Property Title As String
        Public Property Detail As String
        ''' <summary>The same answer in a few words, for the checklists.</summary>
        Public Property Note As String
        ''' <summary>The Check step's findings, one label and value each.</summary>
        Public Property Facts As Object()
    End Class

    Private ReadOnly _store As SettingsStore

    Private _board As StepResult
    Private _firmware As StepResult
    Private _check As StepResult

    Private _ports As String() = New String() {}
    Private _canFlash As Boolean
    Private _busy As Boolean

    ''' <summary>The port the firmware last answered on, so the later steps can name it.</summary>
    Private _confirmedPort As String

    Public Sub New(store As SettingsStore)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        _store = store
    End Sub

    ''' <summary>Setup was walked to its end at least once — the gate the interface opens on.</summary>
    Public ReadOnly Property IsComplete As Boolean
        Get
            Return _store.Settings.Input.ArduinoReady
        End Get
    End Property

    ''' <summary>The port the user pinned, or Nothing for auto-detect.</summary>
    Private ReadOnly Property PinnedPort As String
        Get
            Dim port = _store.Settings.Input.ArduinoPort
            Return If(String.IsNullOrWhiteSpace(port), Nothing, port.Trim())
        End Get
    End Property

    ' ── Projection ───────────────────────────────────────────────────────────

    ''' <summary>Everything the Setup screen, the flow and the Home card draw.</summary>
    Public Function Describe() As Object
        Dim boardDone = Passed(_board)
        Dim firmwareDone = Passed(_firmware)
        Dim checkDone = Passed(_check)

        Dim currentStep = If(Not boardDone, 1, If(Not firmwareDone, 2, If(Not checkDone, 3, 4)))
        Dim blocker As String = Nothing
        If Not IsComplete Then
            blocker = If(Not boardDone, "Finish Setup first — the board has not been found.",
                      If(Not firmwareDone, "Finish Setup first — the board has no firmware yet.",
                      If(Not checkDone, "Finish Setup first — the board has not been checked.",
                         "Finish Setup first — one click is left.")))
        End If

        Dim script = ArduinoFlasher.FindScript()

        Return New With {
            .complete = IsComplete,
            .currentStep = currentStep,
            .blocker = blocker,
            .steps = New Object() {
                New With {.name = "Board", .done = boardDone, .note = NoteOf(_board, "Not looked for yet")},
                New With {.name = "Firmware", .done = firmwareDone, .note = NoteOf(_firmware, "Not checked yet")},
                New With {.name = "Check", .done = checkDone, .note = NoteOf(_check, "Not run yet")}
            },
            .ports = _ports,
            .port = PinnedPort,
            .board = Card(_board),
            .firmware = New With {
                .verdict = Card(_firmware),
                .canFlash = _canFlash,
                .needsDownload = script IsNot Nothing AndAlso Not ArduinoFlasher.ToolchainPresent(script)
            },
            .check = New With {
                .verdict = Card(_check),
                .facts = If(_check?.Facts, New Object() {})
            }
        }
    End Function

    Private Function Passed(result As StepResult) As Boolean
        Return If(result Is Nothing, IsComplete, result.Passed)
    End Function

    Private Function NoteOf(result As StepResult, notYet As String) As String
        If result IsNot Nothing Then Return result.Note
        Return If(IsComplete, "Set up earlier", notYet)
    End Function

    Private Function Card(result As StepResult) As Object
        If result Is Nothing Then
            Return New With {
                .tone = If(IsComplete, "ok", CStr(Nothing)),
                .title = If(IsComplete, "Passed in an earlier session.", CStr(Nothing)),
                .detail = If(IsComplete, "Nothing has been asked of the board yet this session — run it again to check it now.", CStr(Nothing))
            }
        End If
        Return New With {.tone = result.Tone, .title = result.Title, .detail = result.Detail}
    End Function

    Private Shared Function Pass(tone As String, title As String, detail As String, note As String,
                                 Optional facts As Object() = Nothing) As StepResult
        Return New StepResult With {.Passed = True, .Tone = tone, .Title = title, .Detail = detail, .Note = note, .Facts = facts}
    End Function

    Private Shared Function Fail(title As String, detail As String, note As String) As StepResult
        Return New StepResult With {.Passed = False, .Tone = "danger", .Title = title, .Detail = detail, .Note = note}
    End Function

    Private Sub RequireIdle()
        If _busy Then Throw New InvalidOperationException("The board is busy with the last request — wait for it to finish.")
    End Sub

    ' ── 1. Board ─────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Looks for the board: every COM port, and which of them carries an Arduino-family USB id.
    ''' Touches no port — enumerating is all it does — so the page can repeat it while it waits
    ''' for the cable to go in.
    ''' </summary>
    Public Async Function ScanAsync() As Task
        Dim ports As String() = Await Task.Run(Function() ArduinoLink.AvailablePorts())
        Dim known As String() = Await Task.Run(
            Function() ports.Where(Function(p) ArduinoFlasher.LooksLikeArduino(p)).ToArray())
        _ports = ports

        Dim pinned = PinnedPort
        If pinned IsNot Nothing AndAlso Not ports.Contains(pinned, StringComparer.OrdinalIgnoreCase) Then
            _board = Fail($"{pinned} is not there any more.",
                          "The port picked below is not on this machine right now. Plug the board back " &
                          "in, or switch the picker to Auto-detect.",
                          $"{pinned} missing")
        ElseIf ports.Length = 0 Then
            _board = Fail("No board found.",
                          "Nothing is plugged in, as far as Windows is concerned. In order of how often it " &
                          "is the cause:" & vbLf & vbLf &
                          "1.  The USB cable is charge-only. Most spare cables are. Try another one." & vbLf &
                          "2.  The board is not an ATmega32U4 — a Nano or an Uno cannot do this." & vbLf &
                          "3.  Windows has not recognised it; check Device Manager for an unknown device." &
                          vbLf & vbLf &
                          "Plug it in now — this keeps looking.",
                          "No board found")
        ElseIf known.Length > 0 Then
            _board = Pass("ok",
                          $"Found {If(known.Length = 1, "a board", known.Length & " boards")} on {String.Join(", ", known)}.",
                          "Windows recognises it as an Arduino-family device, which is what the next step " &
                          "needs. Whether the bot's firmware is on it is a separate question, and the next " &
                          "step is where it gets asked.",
                          String.Join(", ", known))
        Else
            _board = Pass("warn",
                          "A serial port is there, but it does not look like an Arduino.",
                          $"Found {String.Join(", ", ports)}, and none of them carries an Arduino, SparkFun " &
                          "or Genuino USB id." & vbLf & vbLf &
                          "That is usually a USB-to-serial adapter, a Bluetooth port or another device " &
                          "entirely, and none of those can ever be a keyboard. If you are sure one of them " &
                          "is your board, pick it below and carry on — the next step will settle it either way.",
                          "Not an Arduino?")
        End If
    End Function

    ''' <summary>
    ''' Pins the board to a port, or back to auto-detect for an empty one. Saved at once, and a
    ''' different port is a different board: everything already proven is about the old one.
    ''' </summary>
    Public Async Function SelectPortAsync(port As String) As Task
        RequireIdle()
        Dim wanted = If(String.IsNullOrWhiteSpace(port), "", port.Trim())
        If wanted.Length > 0 AndAlso
           Not ArduinoLink.AvailablePorts().Contains(wanted, StringComparer.OrdinalIgnoreCase) Then
            Throw New ArgumentException($"{wanted} is not a port on this machine.")
        End If

        Dim input = _store.Settings.Input
        If Not String.Equals(wanted, If(input.ArduinoPort, ""), StringComparison.OrdinalIgnoreCase) Then
            input.ArduinoPort = wanted
            _store.Save()
            ArduinoLink.Instance.PreferredPort = wanted
            _confirmedPort = Nothing
            Const again As String = "The port changed, so what was proven before was about another board. Run it again."
            _firmware = New StepResult With {.Title = "Not asked on this port yet.", .Detail = again, .Note = "Not checked yet"}
            _check = New StepResult With {.Title = "Not run on this port yet.", .Detail = again, .Note = "Not run yet"}
            Logger.Info("Setup", $"Board port set to {If(wanted.Length = 0, "auto-detect", wanted)}")
        End If

        Await ScanAsync()
    End Function

    ' ── 2. Firmware ──────────────────────────────────────────────────────────

    ''' <summary>
    ''' Asks the board who it is. A board running wk_hid answers the handshake; one that has never
    ''' been flashed answers nothing, and then the only thing offered is flashing it.
    '''
    ''' Connecting also calibrates the pointer, so the cursor jumps three times the first time
    ''' this answers.
    ''' </summary>
    Public Async Function CheckFirmwareAsync() As Task
        RequireIdle()
        _busy = True
        Try
            Await CheckFirmwareCoreAsync()
        Finally
            _busy = False
        End Try
    End Function

    Private Async Function CheckFirmwareCoreAsync() As Task
        Dim link As ArduinoLink = ArduinoLink.Instance
        link.PreferredPort = PinnedPort
        link.RetryNow()
        Dim ok As Boolean = Await Task.Run(Function() link.EnsureConnected())
        _canFlash = False

        If ok AndAlso link.FirmwareVersion < ArduinoLink.CurrentFirmware Then
            ' A board wk flashed: it types, but it has no pointer that clicks in place.
            _confirmedPort = link.PortName
            Dim script = ArduinoFlasher.FindScript()
            _canFlash = script IsNot Nothing
            _firmware = Fail($"The board runs an older wk_hid (v{link.FirmwareVersion}).",
                             $"It answered the handshake on {link.PortName}, but as version " &
                             $"{link.FirmwareVersion}. This bot clicks where the cursor is without moving it, " &
                             $"which takes version {ArduinoLink.CurrentFirmware} — so the board has to be " &
                             "flashed once more. wk works with the new version as well." & vbLf & vbLf &
                             If(script Is Nothing,
                                "arduino\flash.ps1 is missing, so this copy of the bot cannot flash it.",
                                If(ArduinoFlasher.ToolchainPresent(script),
                                   "Flashing takes about half a minute.",
                                   "The first flash downloads the Arduino build tools — a few hundred " &
                                   "megabytes, once. It asks before it starts.")),
                             $"Firmware v{link.FirmwareVersion} — update")
            Return
        End If

        If ok Then
            _confirmedPort = link.PortName
            _firmware = Pass("ok", $"The firmware is on the board ({link.PortName}).",
                             "It answered the handshake with the current version, so there is nothing to " &
                             "do here. Flashing it again would be harmless but pointless.",
                             $"wk_hid v{link.FirmwareVersion} on {link.PortName}")
            Return
        End If

        ' Not flashed — or not reachable. Those need different answers, and the difference is
        ' whether a port exists at all.
        If ArduinoLink.AvailablePorts().Length = 0 Then
            _firmware = Fail("The board is gone.",
                             "There is no serial port any more — it was unplugged, or it reset. Go back a " &
                             "step and plug it in again.",
                             "Board gone")
        ElseIf link.LastError IsNot Nothing AndAlso
               link.LastError.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0 Then
            _firmware = Fail("Another program is holding the board.",
                             "Close any other copy of the bot, serial monitor or Arduino IDE that has the " &
                             "port open, then ask again." & vbLf & vbLf &
                             "Windows lets exactly one program own a serial port at a time.",
                             "Port busy")
        Else
            Dim script = ArduinoFlasher.FindScript()
            _canFlash = script IsNot Nothing
            _firmware = Fail("This board has no firmware on it yet.",
                             "It answered nothing when asked who it is, which is what a board that has " &
                             "never been flashed does. Until the firmware is on it, it is a serial port and " &
                             "nothing else — it cannot press a key, and there is nothing to connect to." &
                             vbLf & vbLf &
                             If(script Is Nothing,
                                "arduino\flash.ps1 is missing, so this copy of the bot cannot flash it. Take " &
                                "the arduino folder from the source tree, or rebuild.",
                                If(ArduinoFlasher.ToolchainPresent(script),
                                   "Flashing takes about half a minute.",
                                   "The first flash downloads the Arduino build tools — a few hundred " &
                                   "megabytes, once. It asks before it starts.")),
                             "No firmware")
        End If
    End Function

    ''' <summary>
    ''' Flashes wk_hid onto the board and asks it again afterwards. Returns the sentence to show.
    '''
    ''' <paramref name="confirmed"/> is the user having agreed to the toolchain download; it is
    ''' asked in the page, next to the button, and refused here when it is needed and missing.
    ''' <paramref name="progress"/> receives every line the upload prints — the first run spends
    ''' minutes downloading, and a spinner with nothing behind it reads as a hang.
    ''' </summary>
    Public Async Function FlashAsync(confirmed As Boolean, progress As Action(Of String)) As Task(Of String)
        RequireIdle()

        Dim script As String = ArduinoFlasher.FindScript()
        If script Is Nothing Then
            Throw New InvalidOperationException("arduino\flash.ps1 is missing, so this copy of the bot cannot flash a board.")
        End If

        Dim port As String = PinnedPort
        If port Is Nothing Then
            Dim ports As String() = ArduinoLink.AvailablePorts()
            If ports.Length <> 1 Then
                Throw New InvalidOperationException(
                    "Pick which port is the board on the Board step first — flashing the wrong device " &
                    "is not something this can take back.")
            End If
            port = ports(0)
        End If

        If Not confirmed AndAlso Not ArduinoFlasher.ToolchainPresent(script) Then
            Throw New InvalidOperationException("The build tools have to be downloaded first — confirm the download, then flash.")
        End If

        _busy = True
        Try
            Dim fqbn As String = Await Task.Run(Function() ArduinoFlasher.DetectFqbn(port))
            Logger.Info(ArduinoLink.LogSource, $"Flashing {port} as {fqbn} — {script}")

            ' Stand aside so the script can reset the board: it does that by opening the port at
            ' 1200 baud, which it cannot do while we hold it. Disconnect, not Dispose — Dispose
            ' would mark the link dead and the reconnect below could never succeed.
            ArduinoLink.Instance.Disconnect()

            Dim failure As String = Await Task.Run(Function() ArduinoFlasher.Run(script, port, fqbn, progress))
            If failure IsNot Nothing Then
                _canFlash = True
                _firmware = Fail("Flashing failed.", failure & vbLf & vbLf & "The full output is in the log.", "Flash failed")
                Return "Flashing failed — " & failure
            End If

            ' The board re-enumerates after an upload and Windows usually gives it a different
            ' port number, so whatever was pinned is now wrong.
            progress?.Invoke("Flashed. Waiting for the board to come back…")
            Await Task.Delay(2500)
            If PinnedPort IsNot Nothing Then
                _store.Settings.Input.ArduinoPort = ""
                _store.Save()
                Logger.Info("Setup", "Board port set back to auto-detect — flashing renumbers the port")
            End If

            Await ScanAsync()
            Await CheckFirmwareCoreAsync()
        Finally
            _busy = False
        End Try

        Return If(_firmware.Passed,
                  "Flashed — the board answers as wk_hid now.",
                  "Flashed, but the board did not answer afterwards — ask it again in a moment.")
    End Function

    ' ── 3. Check ─────────────────────────────────────────────────────────────

    ''' <summary>
    ''' End to end: connect, calibrate the pointer against this desktop, and read back what the
    ''' board believes it is holding. Connecting is not the same as working.
    ''' </summary>
    Public Async Function CheckAsync() As Task
        RequireIdle()
        _busy = True
        Try
            Dim link As ArduinoLink = ArduinoLink.Instance
            link.PreferredPort = PinnedPort
            link.RetryNow()

            Dim connected As Boolean = Await Task.Run(Function() link.EnsureConnected())
            If Not connected Then
                _check = Fail("Could not reach the board.",
                              If(link.LastError, "It stopped answering.") & vbLf & vbLf &
                              "Go back a step and check the firmware again.",
                              "Not reachable")
                Return
            End If

            If link.FirmwareVersion < ArduinoLink.CurrentFirmware Then
                _check = Fail("The firmware is out of date.",
                              $"The board runs wk_hid v{link.FirmwareVersion}, and clicking where the cursor " &
                              $"is takes v{ArduinoLink.CurrentFirmware}. Go back a step and flash it.",
                              $"Firmware v{link.FirmwareVersion}")
                Return
            End If

            Dim calibrated As Boolean = Await Task.Run(Function() link.Calibrate())
            Dim state As String = Await Task.Run(Function() link.ReadBoardState())
            _confirmedPort = link.PortName

            If state Is Nothing Then
                _check = Fail("The board stopped answering mid-check.",
                              "It opened and then went quiet, which usually means the cable is loose or " &
                              "the board is resetting. Try another USB port.",
                              "Went quiet")
                Return
            End If

            Dim facts = New Object() {
                New With {.label = "Port", .value = link.PortName},
                New With {.label = "Firmware", .value = $"wk_hid v{link.FirmwareVersion} — clicks in place"},
                New With {.label = "Pointer", .value = If(calibrated, "The cursor followed the board", "The cursor did not follow")},
                New With {.label = "Keys", .value = "The board reports holding none, as it should"}
            }

            If calibrated Then
                _check = Pass("ok", "Everything answered.",
                              "This is as far as a check can go without the game. Whether the game takes " &
                              "the keys is for the Orb Walker to show.",
                              "Pointer follows", facts)
            Else
                _check = Pass("warn", "It works, but the cursor did not follow the board.",
                              $"The board is answering on {link.PortName} and the keyboard is fine. What " &
                              "failed is the pointer check — the cursor did not move with the probes, which " &
                              "usually means something was holding it still." & vbLf & vbLf &
                              "Clicks do not depend on it: they go through the board's other pointer, in " &
                              "place. If they do nothing in the game, run this again with the mouse left alone.",
                              "Pointer did not follow", facts)
            End If
        Finally
            _busy = False
        End Try
    End Function

    ' ── 4. Done ──────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Closes the flow for good: the board was found, flashed and checked, and the rest of the
    ''' interface opens — on this launch and every later one.
    ''' </summary>
    Public Sub Finish()
        If Not (Passed(_board) AndAlso Passed(_firmware) AndAlso Passed(_check)) Then
            Throw New InvalidOperationException("Every step has to pass before Setup can finish.")
        End If
        If IsComplete Then Return

        _store.Settings.Input.ArduinoReady = True
        _store.Save()
        Logger.Info("Setup", $"Setup finished — the board on {If(_confirmedPort, "auto-detect")} " &
                             "presses every key and click")
    End Sub

End Class
