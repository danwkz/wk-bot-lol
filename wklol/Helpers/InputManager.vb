Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Windows.Forms

' ============================================================
'  InputManager.vb  -  Keys and clicks, through the Arduino
'
'  wk's InputManager drives three wires (Simulate, Control,
'  Hardware) and a humanised mouse. This one drives ONE: the
'  board, with Windows seeing an ordinary keyboard and mouse.
'  What it keeps is the part that is not about the wire — the
'  hold times that make a key readable to a game that samples
'  input once per frame.
' ============================================================

#Region "Win32 Declarations"

Module Win32

    <DllImport("user32.dll")>
    Public Function GetCursorPos(<Out> ByRef lpPoint As NATIVEPOINT) As Boolean
    End Function

    <DllImport("user32.dll")>
    Public Function GetForegroundWindow() As IntPtr
    End Function

    <StructLayout(LayoutKind.Sequential)>
    Public Structure NATIVEPOINT
        Public X As Integer
        Public Y As Integer
    End Structure

    ' ── Multimedia timer resolution ───────────────────────────────────────────────────────
    ' Thread.Sleep and every timed wait are rounded UP to the system scheduler tick, which is
    ' 15.6 ms out of the box. timeBeginPeriod buys 1 ms ticks for the calling process and MUST be
    ' paired with timeEndPeriod. See PrecisionTimerScope.

    <DllImport("winmm.dll")>
    Public Function timeBeginPeriod(uPeriod As UInteger) As UInteger
    End Function

    <DllImport("winmm.dll")>
    Public Function timeEndPeriod(uPeriod As UInteger) As UInteger
    End Function

End Module

#End Region

''' <summary>
''' Holds the multimedia timer at 1 ms for as long as it lives.
'''
''' Windows rounds every wait UP to the scheduler tick — 15.6 ms by default — so a delay the user
''' typed as 100 ms would come out as 109, and a 30 ms key hold as 31 or 46. The resolution is
''' bought for the length of one run of the Orb Walker and given straight back, because holding it
''' raises the machine's idle power draw.
'''
''' Failure is not fatal and not worth a log line: the waits still happen, only coarser.
''' </summary>
Friend NotInheritable Class PrecisionTimerScope
    Implements IDisposable

    Private ReadOnly _raised As Boolean

    Public Sub New()
        _raised = (Win32.timeBeginPeriod(1UI) = 0UI)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _raised Then Win32.timeEndPeriod(1UI)
    End Sub

End Class

''' <summary>
''' Presses keys and clicks the mouse through <see cref="ArduinoLink"/>. Every method answers
''' False when the board did not take the command — there is no fallback wire, see ArduinoLink.
'''
''' Keys go to whatever window is in FRONT, exactly as from a real keyboard. The layout they are
''' resolved against is that window's too, so the key the bot asks for is the key the game reads.
''' </summary>
Public Class InputManager

    Private ReadOnly _keyHoldMin As Integer
    Private ReadOnly _keyHoldMax As Integer
    Private ReadOnly _clickHoldMin As Integer
    Private ReadOnly _clickHoldMax As Integer
    Private ReadOnly _rng As New Random()

    Public Sub New(keyHoldMin As Integer, keyHoldMax As Integer, clickHoldMin As Integer, clickHoldMax As Integer)
        _keyHoldMin = Math.Min(keyHoldMin, keyHoldMax)
        _keyHoldMax = Math.Max(keyHoldMin, keyHoldMax)
        _clickHoldMin = Math.Min(clickHoldMin, clickHoldMax)
        _clickHoldMax = Math.Max(clickHoldMin, clickHoldMax)
    End Sub

    ''' <summary>
    ''' The manager a module wants: built straight from the stored input settings. The board is
    ''' shared by every manager, so the port the user pinned is published once here rather than
    ''' being carried around by each of them.
    ''' </summary>
    Public Shared Function FromConfig(config As InputConfig) As InputManager
        If config Is Nothing Then config = New InputConfig()
        ArduinoLink.Instance.PreferredPort = config.ArduinoPort
        Return New InputManager(config.KeyHoldMin, config.KeyHoldMax, config.ClickHoldMin, config.ClickHoldMax)
    End Function

    ' ================================================================
    '  KEYBOARD
    ' ================================================================

    ''' <summary>
    ''' Puts a key down and LEAVES IT DOWN until <see cref="KeyUp"/>. The board keeps saying it is
    ''' held — its heartbeat stops the firmware watchdog releasing it — so the caller owns the
    ''' release and must make it from a Finally.
    ''' </summary>
    Public Function KeyDown(key As Keys) As Boolean
        Return ArduinoLink.Instance.KeyEvent(key, keyUp:=False, targetHwnd:=Win32.GetForegroundWindow())
    End Function

    ''' <summary>Lets go of a key. Safe to call for a key that is not down.</summary>
    Public Function KeyUp(key As Keys) As Boolean
        Return ArduinoLink.Instance.KeyEvent(key, keyUp:=True, targetHwnd:=Win32.GetForegroundWindow())
    End Function

    ''' <summary>
    ''' Presses and releases one key. It stays down for the configured key-press time: a keystroke
    ''' whose down and up arrive together is routinely dropped by a game that samples input once
    ''' per frame, so the hold has to outlast a frame to be seen reliably.
    '''
    ''' Returns False when the press did not land. The release is sent either way — a key left
    ''' down is the one state this must never leave behind.
    ''' </summary>
    Public Function PressKey(key As Keys) As Boolean
        If Not KeyDown(key) Then Return False
        Try
            Thread.Sleep(RandBetween(_keyHoldMin, _keyHoldMax))
        Finally
            KeyUp(key)
        End Try
        Return True
    End Function

    ' ================================================================
    '  MOUSE
    ' ================================================================

    ''' <summary>
    ''' Clicks where the cursor is now, and leaves it there.
    '''
    ''' THE POINTER IS NOT TOUCHED. The click goes through the board's relative pointer, whose
    ''' reports carry a button and zero movement — what a real mouse sends when it is clicked in
    ''' place — so Windows applies it wherever the cursor is at that instant, however the player is
    ''' moving it. It used to read the cursor and send the board there first, through its absolute
    ''' pointer; that nudged the cursor by whatever the calibration was off on every click, and
    ''' snapped it back under a moving hand. See ArduinoLink.ClickEvent.
    ''' </summary>
    Public Function ClickAtCursor(rightButton As Boolean) As Boolean
        Dim link As ArduinoLink = ArduinoLink.Instance
        Dim button As Integer = If(rightButton, ArduinoLink.ButtonRight, ArduinoLink.ButtonLeft)
        If Not link.ClickEvent(button, down:=True) Then Return False
        Try
            Thread.Sleep(RandBetween(_clickHoldMin, _clickHoldMax))
        Finally
            ' Up even if the wait threw: a button left down turns the next click into a drag.
            link.ClickEvent(button, down:=False)
        End Try
        Return True
    End Function

    ' ================================================================
    '  UTILITIES
    ' ================================================================

    Private Function RandBetween(min As Integer, max As Integer) As Integer
        Return _rng.Next(min, max + 1)
    End Function

End Class
