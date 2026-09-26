Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Globalization
Imports System.IO.Ports
Imports System.Text
Imports System.Threading
Imports System.Windows.Forms
Imports wklol.Core

''' <summary>
''' The bot's end of the Arduino that carries every key and click.
'''
''' ── Why this is a singleton ────────────────────────────────────────────────────────────────
''' There is ONE board and one serial port, but an <see cref="InputManager"/> is rebuilt every
''' time a setting changes. A link owned by the manager would be opened and closed on every edit,
''' and two managers interleaving half-written commands down one port would produce keystrokes
''' neither of them asked for. So the link lives here, outlives every manager, and serialises the
''' whole send-and-acknowledge exchange under one lock.
'''
''' ── The contract with the caller ───────────────────────────────────────────────────────────
''' Every method answers False when the command did not reach the board, and NOTHING falls back to
''' SendInput. The board exists so the input carries no LLMHF_INJECTED flag; quietly serving
''' injected events when the cable is loose would defeat that, invisibly. A refused command
''' surfaces as a False, a log line says why, and the caller skips its action.
'''
''' ── Clicks never move the pointer ──────────────────────────────────────────────────────────
''' The board carries two pointers. The ABSOLUTE one (wk's) says where the cursor goes, so a button
''' pressed through it lands wherever the board last put the cursor; clicking "where the cursor is"
''' through it meant reading the cursor, mapping it through a calibration and sending it back —
''' which moved the pointer by whatever the mapping was off, and dragged it out from under a hand
''' that was moving it. The RELATIVE one (firmware v2) clicks with zero movement, the way a real
''' mouse does, so the click is exactly where the cursor is. It is the only one this bot clicks with.
''' </summary>
Public NotInheritable Class ArduinoLink
    Implements IDisposable

    Public Const LogSource As String = "Arduino"

    ''' <summary>Identify string the firmware answers "P" with, followed by its protocol version.</summary>
    Private Const FirmwareBanner As String = "WKHID"

    ''' <summary>
    ''' The protocol version of the wk_hid in arduino\ beside this bot — the first one with the
    ''' relative pointer that clicks in place. A board that answers with less was flashed by wk (or
    ''' an older copy of this bot) and has to be flashed again before it can click.
    ''' </summary>
    Public Const CurrentFirmware As Integer = 2

    ''' <summary>
    ''' Never 1200. Opening a 32U4's CDC port at 1200 baud is the documented signal to jump into
    ''' the bootloader — the board would vanish from the port list and stop being a keyboard,
    ''' which is exactly what the upload script does on purpose.
    ''' </summary>
    Private Const PortBaud As Integer = 115200

    Private Const HandshakeTimeoutMs As Integer = 400
    Private Const CommandTimeoutMs As Integer = 250

    ''' <summary>How long a failed connection attempt is remembered. Without it a bot with no board
    ''' would open every COM port on the machine on every key it tries to press.</summary>
    Private Const ReconnectCooldownMs As Long = 3000

    ''' <summary>Gap between heartbeats while something is held. Comfortably inside the firmware's
    ''' 2500 ms watchdog, so a deliberate hold — the Orb Walker holds its key for as long as the
    ''' master key is down — is never mistaken for an abandoned one.</summary>
    Private Const HeartbeatMs As Integer = 700

    ''' <summary>Logical units the pointer range spans on each axis.</summary>
    Private Const LogicalMax As Integer = 32767

    ''' <summary>
    ''' Whether the timestamps below mean anything yet. A flag rather than a sentinel value:
    ''' Long.MinValue overflows on "now - never", and zero is INSIDE the cooldown for the first
    ''' three seconds of the app, so the first connection attempt would never happen.
    ''' </summary>
    Private _hasAttempted As Boolean
    Private _hasWarned As Boolean

    ' Buttons, as the firmware numbers them.
    Public Const ButtonLeft As Integer = 1
    Public Const ButtonRight As Integer = 2

    Private Shared ReadOnly _instance As New ArduinoLink()

    Public Shared ReadOnly Property Instance As ArduinoLink
        Get
            Return _instance
        End Get
    End Property

    ' Guards the port and every piece of state below it. Held for the whole send-and-read
    ' exchange: a reply belongs to exactly one command, and two threads sharing the stream would
    ' each read the other's.
    Private ReadOnly _gate As New Object()

    Private _port As SerialPort
    Private _portName As String
    Private _preferredPort As String = Nothing
    Private _lastAttemptMs As Long
    Private _lastError As String
    Private _disposed As Boolean

    ''' <summary>The protocol version the board answered the handshake with; 0 while not connected.</summary>
    Private _firmwareVersion As Integer

    ' What the board is holding for us, so a release can be exact and the heartbeat knows whether
    ' it is needed at all.
    Private ReadOnly _heldUsages As New HashSet(Of Byte)()
    Private _heldButtons As Integer

    Private _heartbeat As Thread
    Private _heartbeatStop As Boolean

    ' Warnings are the same sentence over and over once the cable is out; without this the log
    ' becomes unreadable at exactly the moment it is being read.
    Private _lastWarnMs As Long
    Private Const WarnCooldownMs As Long = 5000

    Private Sub New()
        ' A held key with the app gone is the worst state this feature can reach, and the firmware
        ' watchdog only catches it after 2.5 seconds. Releasing on the way out closes that window
        ' for every ordinary exit; the watchdog stays for the ones that are not ordinary.
        AddHandler AppDomain.CurrentDomain.ProcessExit, Sub() TryReleaseQuietly()
    End Sub

#Region "Status"

    ''' <summary>True when the board is open and answered the handshake.</summary>
    Public ReadOnly Property IsConnected As Boolean
        Get
            SyncLock _gate
                Return _port IsNot Nothing AndAlso _port.IsOpen
            End SyncLock
        End Get
    End Property

    ''' <summary>
    ''' The protocol version of the firmware on the connected board — 1 for wk's, 2 from the one
    ''' that clicks in place — or 0 while nothing is connected. Compare with
    ''' <see cref="CurrentFirmware"/>.
    ''' </summary>
    Public ReadOnly Property FirmwareVersion As Integer
        Get
            SyncLock _gate
                Return _firmwareVersion
            End SyncLock
        End Get
    End Property

    ''' <summary>The COM port in use, or Nothing.</summary>
    Public ReadOnly Property PortName As String
        Get
            SyncLock _gate
                Return _portName
            End SyncLock
        End Get
    End Property

    ''' <summary>Why the last attempt failed, for Setup to show.</summary>
    Public ReadOnly Property LastError As String
        Get
            SyncLock _gate
                Return _lastError
            End SyncLock
        End Get
    End Property

    ''' <summary>
    ''' The port the user pinned in Setup, or Nothing/empty for "find it".
    '''
    ''' Changing it drops the current connection rather than waiting for it to fail, so picking a
    ''' port takes effect on the next command like every other input setting.
    ''' </summary>
    Public Property PreferredPort As String
        Get
            SyncLock _gate
                Return _preferredPort
            End SyncLock
        End Get
        Set(value As String)
            Dim wanted As String = If(String.IsNullOrWhiteSpace(value), Nothing, value.Trim())
            SyncLock _gate
                If String.Equals(wanted, _preferredPort, StringComparison.OrdinalIgnoreCase) Then Return
                _preferredPort = wanted
                If _port IsNot Nothing AndAlso
                   Not String.Equals(wanted, _portName, StringComparison.OrdinalIgnoreCase) Then
                    ClosePortLocked("port changed in Setup")
                End If
                ' A deliberate change deserves an immediate retry rather than the cooldown a
                ' failure earns.
                _hasAttempted = False
            End SyncLock
        End Set
    End Property

    ''' <summary>
    ''' Forgets the last failed attempt, so the next connection is tried now rather than after the
    ''' cooldown. For a person pressing a button to look again: the cooldown protects the hot path
    ''' from scanning ports on every key, and a deliberate check is not the hot path.
    ''' </summary>
    Public Sub RetryNow()
        SyncLock _gate
            If _port Is Nothing OrElse Not _port.IsOpen Then _hasAttempted = False
        End SyncLock
    End Sub

    ''' <summary>One line describing the link, for the Settings screen.</summary>
    Public Function DescribeStatus() As String
        SyncLock _gate
            If _port Is Nothing OrElse Not _port.IsOpen Then
                If Not _hasAttempted Then Return "Not connected — nothing has asked for the board yet."
                Return "Not connected — " & WhyNotConnected()
            End If
            Return $"Connected on {_portName} · wk_hid v{_firmwareVersion}" &
                   If(_firmwareVersion < CurrentFirmware, $" — flash it in Setup to update to v{CurrentFirmware}", "")
        End SyncLock
    End Function

    ''' <summary>Every COM port on the machine, for the port picker.</summary>
    Public Shared Function AvailablePorts() As String()
        Try
            Dim names As String() = SerialPort.GetPortNames()
            Array.Sort(names, AddressOf ComparePortNames)
            Return names
        Catch
            Return New String() {}
        End Try
    End Function

    ' COM10 sorts before COM9 alphabetically, which reads as a bug in a dropdown.
    Private Shared Function ComparePortNames(a As String, b As String) As Integer
        Dim na As Integer, nb As Integer
        If TryPortNumber(a, na) AndAlso TryPortNumber(b, nb) Then Return na.CompareTo(nb)
        Return String.Compare(a, b, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function TryPortNumber(name As String, ByRef number As Integer) As Boolean
        number = 0
        If name Is Nothing OrElse Not name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) Then Return False
        Return Integer.TryParse(name.Substring(3), number)
    End Function

#End Region

#Region "Connection"

    ''' <summary>
    ''' Opens the board if it is not already open. Cheap on the hot path: a live connection is one
    ''' field read, and a failed attempt is remembered for <see cref="ReconnectCooldownMs"/> so a
    ''' bot running without a board does not scan the machine's COM ports on every keystroke.
    ''' </summary>
    Public Function EnsureConnected() As Boolean
        SyncLock _gate
            Return EnsureConnectedLocked()
        End SyncLock
    End Function

    Private Function EnsureConnectedLocked() As Boolean
        If _disposed Then Return False
        If _port IsNot Nothing AndAlso _port.IsOpen Then Return True

        Dim now As Long = Clock.NowMs
        If _hasAttempted AndAlso now - _lastAttemptMs < ReconnectCooldownMs Then Return False
        _hasAttempted = True
        _lastAttemptMs = now

        Dim candidates As New List(Of String)()
        If Not String.IsNullOrWhiteSpace(_preferredPort) Then
            candidates.Add(_preferredPort)
        Else
            candidates.AddRange(AvailablePorts())
        End If

        If candidates.Count = 0 Then
            _lastError = "no COM port on this machine — check the cable, and that it is a DATA cable"
            Return False
        End If

        Dim failures As New List(Of String)()
        For Each name As String In candidates
            Dim why As String = Nothing
            If TryOpenLocked(name, why) Then
                _lastError = Nothing
                Logger.Info(LogSource, $"Board found on {name} — wk_hid v{_firmwareVersion}")
                StartHeartbeatLocked()
                ' No calibration here, unlike wk: nothing in this bot positions the pointer, and
                ' measuring it jumps the cursor three times — on a reconnect, in the middle of a
                ' run, under the player's hand. Setup's Check measures it when asked to.
                Return True
            End If
            failures.Add($"{name}: {why}")
        Next

        _lastError = String.Join("; ", failures)
        Return False
    End Function

    ''' <summary>Opens one port and asks who is on it. Only a board that answers the banner is
    ''' kept — every other COM device on the machine gets its port handed straight back.</summary>
    Private Function TryOpenLocked(name As String, ByRef failure As String) As Boolean
        Dim candidate As SerialPort = Nothing
        Try
            candidate = New SerialPort(name, PortBaud, Parity.None, 8, StopBits.One) With {
                .ReadTimeout = HandshakeTimeoutMs,
                .WriteTimeout = CommandTimeoutMs,
                .NewLine = vbLf,
                .Encoding = Encoding.ASCII,
                .DtrEnable = True,
                .RtsEnable = True
            }
            candidate.Open()
            candidate.DiscardInBuffer()
            candidate.DiscardOutBuffer()

            ' The 32U4's CDC endpoint is not ready the instant the port opens, and the first ping
            ' is routinely lost to that. Two more attempts cost 300 ms on a port that will never
            ' answer, and save a reconnect cycle on the one that will.
            '
            ' Deliberately NOT through ExchangeLocked: that treats a silent port as a link that
            ' has died and says so in the log, which is right once the board is ours and pure
            ' noise while we are still knocking on every COM port on the machine.
            For attempt As Integer = 1 To 3
                Thread.Sleep(60)
                Try
                    candidate.DiscardInBuffer()
                    candidate.WriteLine("P")
                    Dim reply As String = candidate.ReadLine()
                    If reply IsNot Nothing AndAlso
                       reply.Trim().StartsWith(FirmwareBanner, StringComparison.Ordinal) Then
                        candidate.ReadTimeout = CommandTimeoutMs
                        _port = candidate
                        _portName = name
                        _firmwareVersion = VersionOf(reply)
                        ' Whatever a previous session or a reset left held is not ours to keep.
                        ExchangeLocked("R")
                        _heldUsages.Clear()
                        _heldButtons = 0
                        failure = Nothing
                        Return True
                    End If
                Catch ex As TimeoutException
                    ' Something else is on this port, or the board is still enumerating. Retry.
                End Try
            Next

            failure = "opened, but nothing answered the handshake — is wk_hid flashed?"
        Catch ex As UnauthorizedAccessException
            failure = "port is busy (another program has it open)"
        Catch ex As Exception
            failure = ex.Message
        End Try

        ' Not ours: close it and forget it, so the next candidate is tried on a clean slate.
        _port = Nothing
        _portName = Nothing
        Try
            If candidate IsNot Nothing Then
                If candidate.IsOpen Then candidate.Close()
                candidate.Dispose()
            End If
        Catch
        End Try
        Return False
    End Function

    ''' <param name="deliberate">
    ''' True when the caller MEANT to let the port go — shutting down, or standing aside so the
    ''' board can be flashed. Only the level and the wording of the log line change.
    ''' </param>
    ''' <summary>
    ''' The protocol version in a handshake reply — "WKHID 2" is 2. A reply with no number is the
    ''' first version, which never sent one worth reading.
    ''' </summary>
    Private Shared Function VersionOf(reply As String) As Integer
        Dim rest As String = reply.Trim().Substring(FirmwareBanner.Length).Trim()
        Dim version As Integer
        If Integer.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, version) AndAlso version > 0 Then
            Return version
        End If
        Return 1
    End Function

    Private Sub ClosePortLocked(why As String, Optional deliberate As Boolean = False)
        StopHeartbeatLocked()
        _firmwareVersion = 0
        _heldUsages.Clear()
        _heldButtons = 0
        Dim closing As SerialPort = _port
        _port = Nothing
        Dim was As String = _portName
        _portName = Nothing
        If closing Is Nothing Then Return
        Try
            If closing.IsOpen Then closing.Close()
            closing.Dispose()
        Catch
        End Try
        _lastError = why
        Dim board As String = If(was, "the board")
        If deliberate Then
            Logger.Info(LogSource, $"Let go of {board} — {why}")
        Else
            Logger.Warn(LogSource, $"Link to {board} dropped — {why}")
        End If
    End Sub

#End Region

#Region "Protocol"

    ''' <summary>
    ''' Sends one command and returns the board's reply line, or Nothing when it did not answer.
    ''' The caller must hold <see cref="_gate"/>.
    '''
    ''' Unsolicited "EV …" lines — the firmware announcing its own watchdog release — are logged
    ''' and skipped rather than returned, or they would be read as the answer to whatever command
    ''' happened to follow them and put every later reply one behind.
    ''' </summary>
    Private Function ExchangeLocked(command As String) As String
        Dim port As SerialPort = _port
        If port Is Nothing OrElse Not port.IsOpen Then Return Nothing

        Try
            port.WriteLine(command)
            For skipped As Integer = 0 To 4
                Dim line As String = port.ReadLine()
                If line Is Nothing Then Return Nothing
                line = line.Trim()
                If line.Length = 0 Then Continue For
                If line.StartsWith("EV ", StringComparison.Ordinal) Then
                    Logger.Warn(LogSource, $"Board reported: {line.Substring(3)}")
                    ' Its watchdog fired, so it is holding nothing now whatever we believed.
                    _heldUsages.Clear()
                    _heldButtons = 0
                    Continue For
                End If
                Return line
            Next
            Return Nothing
        Catch ex As TimeoutException
            ClosePortLocked("the board stopped answering")
            Return Nothing
        Catch ex As Exception
            ClosePortLocked(ex.Message)
            Return Nothing
        End Try
    End Function

    ''' <summary>Sends a command that is expected to answer OK. False means it did not.</summary>
    Private Function CommandLocked(command As String) As Boolean
        If Not EnsureConnectedLocked() Then
            WarnThrottled($"Hardware input skipped — {WhyNotConnected()}")
            Return False
        End If

        Dim reply As String = ExchangeLocked(command)
        If reply Is Nothing Then Return False
        If String.Equals(reply, "OK", StringComparison.Ordinal) Then Return True

        WarnThrottled($"Board refused '{command}' — {reply}")
        Return False
    End Function

    ''' <summary>
    ''' Why there is no board, in words. <see cref="_lastError"/> is cleared on a successful
    ''' connect, so after a deliberate shutdown it is empty — and a log line that trails off after
    ''' "skipped —" tells the reader nothing at all.
    ''' </summary>
    Private Function WhyNotConnected() As String
        If Not String.IsNullOrWhiteSpace(_lastError) Then Return _lastError
        If _disposed Then Return "the link has been shut down"
        Return "the board is not connected"
    End Function

    Private Sub WarnThrottled(message As String)
        Dim now As Long = Clock.NowMs
        If _hasWarned AndAlso now - _lastWarnMs < WarnCooldownMs Then Return
        _hasWarned = True
        _lastWarnMs = now
        Logger.Warn(LogSource, message)
    End Sub

#End Region

#Region "Keyboard"

    ''' <summary>
    ''' Presses or releases one virtual key on the board. <paramref name="targetHwnd"/> only
    ''' selects the keyboard layout the virtual key is resolved against — see
    ''' <see cref="HidKeyMap"/> — and may be Zero.
    ''' </summary>
    Public Function KeyEvent(key As Keys, keyUp As Boolean, targetHwnd As IntPtr) As Boolean
        Dim usage As Byte = HidKeyMap.UsageFor(key, HidKeyMap.LayoutFor(targetHwnd))
        If usage = HidKeyMap.NoUsage Then
            WarnThrottled($"No physical key matches {key} on this keyboard layout — nothing sent")
            Return False
        End If

        SyncLock _gate
            Dim ok As Boolean = CommandLocked(If(keyUp, "KU ", "KD ") & usage.ToString(CultureInfo.InvariantCulture))
            If ok Then
                If keyUp Then _heldUsages.Remove(usage) Else _heldUsages.Add(usage)
            ElseIf Not keyUp Then
                ' A press that did not land is not held; recording it would make the heartbeat
                ' chase a key that does not exist.
                _heldUsages.Remove(usage)
            End If
            Return ok
        End SyncLock
    End Function

#End Region

#Region "Mouse"

    ''' <summary>
    ''' Presses or releases a mouse button WHERE THE CURSOR IS, without moving it — the firmware's
    ''' relative pointer (v2), whose reports carry a button and zero movement. 1 = left, 2 = right.
    ''' False on a board still running version 1, which has no such pointer.
    ''' </summary>
    Public Function ClickEvent(button As Integer, down As Boolean) As Boolean
        SyncLock _gate
            If Not EnsureConnectedLocked() Then
                WarnThrottled($"Hardware input skipped — {WhyNotConnected()}")
                Return False
            End If
            If _firmwareVersion < CurrentFirmware Then
                WarnThrottled($"Click skipped — the board runs wk_hid v{_firmwareVersion}, and clicking in " &
                              $"place needs v{CurrentFirmware}. Flash it in Setup.")
                Return False
            End If

            Dim ok As Boolean = CommandLocked(If(down, "CD ", "CU ") & button.ToString(CultureInfo.InvariantCulture))
            Dim bit As Integer = 1 << (button - 1)
            If ok AndAlso down Then
                _heldButtons = _heldButtons Or bit
            Else
                _heldButtons = _heldButtons And Not bit
            End If
            Return ok
        End SyncLock
    End Function

#End Region

#Region "Pointer check"

    ''' <summary>
    ''' Proves the board drives the mouse: three probe moves of its absolute pointer, each read
    ''' back through GetCursorPos. Nothing in this bot positions the pointer, so the measurement is
    ''' not kept — it is Setup's evidence that Windows treats the board as a mouse, and it is logged.
    '''
    ''' The cursor visibly jumps three times while it runs, which is why only Setup's Check asks
    ''' for it — never a connection on its own.
    ''' </summary>
    Public Function Calibrate() As Boolean
        SyncLock _gate
            If Not EnsureConnectedLocked() Then Return False
            Return CalibrateLocked()
        End SyncLock
    End Function

    Private Function CalibrateLocked() As Boolean
        Const near As Integer = 4000
        Const far As Integer = 28000

        Dim origin As Point, alongX As Point, alongY As Point
        If Not ProbeLocked(near, near, origin) Then Return False
        If Not ProbeLocked(far, near, alongX) Then Return False
        If Not ProbeLocked(near, far, alongY) Then Return False

        Dim span As Double = far - near
        Dim sx As Double = (alongX.X - origin.X) / span
        Dim sy As Double = (alongY.Y - origin.Y) / span

        ' A scale near zero means the cursor did not move with the probes: the board is reporting
        ' something Windows is not treating as a pointer, or another program is holding the cursor
        ' still.
        If Math.Abs(sx) < 0.0001 OrElse Math.Abs(sy) < 0.0001 Then
            Logger.Warn(LogSource, "Pointer check failed — the cursor did not follow the board.")
            Return False
        End If

        Dim offsetX As Double = origin.X - sx * near
        Dim offsetY As Double = origin.Y - sy * near
        Logger.Info(LogSource,
                    $"Pointer check passed — the board's range covers " &
                    $"{CInt(Math.Round(offsetX))},{CInt(Math.Round(offsetY))} to " &
                    $"{CInt(Math.Round(offsetX + sx * LogicalMax))},{CInt(Math.Round(offsetY + sy * LogicalMax))}")
        Return True
    End Function

    Private Function SendMoveLocked(logicalX As Integer, logicalY As Integer) As Boolean
        Return CommandLocked($"MM {Clamp(logicalX, 0, LogicalMax)} {Clamp(logicalY, 0, LogicalMax)}")
    End Function

    Private Function ProbeLocked(logicalX As Integer, logicalY As Integer, ByRef landed As Point) As Boolean
        landed = Point.Empty
        If Not SendMoveLocked(logicalX, logicalY) Then Return False
        ' Generous on purpose: this runs once per connection, and a report that has not been
        ' processed yet would be measured as a mapping that does not exist.
        Thread.Sleep(60)
        Dim pt As Win32.NATIVEPOINT
        If Not Win32.GetCursorPos(pt) Then Return False
        landed = New Point(pt.X, pt.Y)
        Return True
    End Function

    Private Shared Function Clamp(value As Integer, low As Integer, high As Integer) As Integer
        If value < low Then Return low
        If value > high Then Return high
        Return value
    End Function

#End Region

#Region "Heartbeat and shutdown"

    ''' <summary>
    ''' Keeps the firmware watchdog quiet while the bot is deliberately holding something. A long
    ''' hold sends nothing else, which is indistinguishable from a crash unless somebody keeps
    ''' saying so. This thread does nothing at all while the board is idle.
    ''' </summary>
    Private Sub StartHeartbeatLocked()
        If _heartbeat IsNot Nothing AndAlso _heartbeat.IsAlive Then Return
        _heartbeatStop = False
        _heartbeat = New Thread(AddressOf HeartbeatLoop) With {
            .IsBackground = True,
            .Name = "ArduinoHeartbeat"
        }
        _heartbeat.Start()
    End Sub

    Private Sub StopHeartbeatLocked()
        _heartbeatStop = True
        _heartbeat = Nothing
    End Sub

    Private Sub HeartbeatLoop()
        While Not _heartbeatStop
            Thread.Sleep(HeartbeatMs)
            If _heartbeatStop Then Exit While
            Try
                SyncLock _gate
                    If _port Is Nothing OrElse Not _port.IsOpen Then Exit While
                    If _heldUsages.Count = 0 AndAlso _heldButtons = 0 Then Continue While
                    ExchangeLocked("H")
                End SyncLock
            Catch
                ' A dead link is handled where the commands are sent; this thread only has to
                ' survive it.
            End Try
        End While
    End Sub

    ''' <summary>
    ''' Releases everything the board is holding — every key and every button — and says whether
    ''' the board acknowledged it. Safe to call when nothing is held, and when there is no board.
    ''' </summary>
    Public Function ReleaseAll() As Boolean
        SyncLock _gate
            _heldUsages.Clear()
            _heldButtons = 0
            If _port Is Nothing OrElse Not _port.IsOpen Then Return False
            Return CommandLocked("R")
        End SyncLock
    End Function

    ''' <summary>
    ''' Lets go of the port, releasing everything first, but leaves the link USABLE — the next
    ''' command opens it again.
    '''
    ''' Different from <see cref="Dispose"/>, and the difference matters exactly once: flashing.
    ''' The upload script resets the board by opening its port at 1200 baud, which it cannot do
    ''' while the bot holds it, so the bot has to stand aside — and then reconnect to the freshly
    ''' flashed board a few seconds later. Dispose marks the link dead for the rest of the session.
    ''' </summary>
    Public Sub Disconnect()
        SyncLock _gate
            Try
                If _port IsNot Nothing AndAlso _port.IsOpen Then ExchangeLocked("R")
            Catch
            End Try
            ClosePortLocked("released the port on purpose", deliberate:=True)
            ' Not a failure, so it must not earn the reconnect cooldown — whoever asked for this
            ' is about to want the board back.
            _hasAttempted = False
            _lastError = Nothing
        End SyncLock
    End Sub

    Private Sub TryReleaseQuietly()
        Try
            ReleaseAll()
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Asks the board what it believes it is holding. Purely diagnostic — Setup's check reads it
    ''' back to prove the round trip works.
    ''' </summary>
    Public Function ReadBoardState() As String
        SyncLock _gate
            If Not EnsureConnectedLocked() Then Return Nothing
            Return ExchangeLocked("S")
        End SyncLock
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock _gate
            If _disposed Then Return
            Try
                ' Straight down the wire, not through CommandLocked — that one tries to RECONNECT
                ' first, which on the way out is both pointless and noisy.
                If _port IsNot Nothing AndAlso _port.IsOpen Then ExchangeLocked("R")
            Catch
            End Try
            _heldUsages.Clear()
            _heldButtons = 0
            _disposed = True
            ClosePortLocked("shutting down", deliberate:=True)
        End SyncLock
    End Sub

#End Region

End Class
