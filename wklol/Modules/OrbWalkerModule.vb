Imports System.Threading
Imports System.Windows.Forms
Imports wklol.Core

Namespace Modules

    ''' <summary>
    ''' Holds one key, and taps another and clicks at the cursor on a beat, for as long as the
    ''' master key is held — or, in toggle mode, from one press of it to the next.
    '''
    ''' <code>
    '''   start ─▶ hold key DOWN
    '''            loop until stop:
    '''               tap the press key
    '''               wait the delay after the press
    '''               click at the cursor
    '''               wait the delay after the click
    '''   stop  ─▶ hold key UP
    '''
    '''   hold mode:    start = master down     stop = master up
    '''   toggle mode:  start = master pressed  stop = master pressed again
    ''' </code>
    '''
    ''' The master key is watched by a global hook the host owns (see BotHost): it reports the two
    ''' edges here, the mode turns them into a start and a stop, and this thread does all the
    ''' pressing. Between runs the tick is parked on the start, so an idle module costs nothing.
    '''
    ''' TWO DELAYS, ONE EACH SIDE OF THE CLICK. The press and the click take turns, each followed by
    ''' its own wait — the rhythm of a hand alternating the two, only steadier. With a single delay
    ''' the next press followed the click at once, and every delay read from outside as "wait, then
    ''' press and click together".
    '''
    ''' STOPPING ENDS IT AT ONCE. A stop during either wait ends the run there, without whatever
    ''' would have followed: the click is a move command, and one sent after the player asked it to
    ''' stop is a move they did not ask for.
    '''
    ''' THE HOLD KEY ALWAYS COMES BACK UP — released from a Finally, so a run ended by the master key,
    ''' by the module being switched off, or by the board failing mid-run all end the same way.
    ''' </summary>
    Public NotInheritable Class OrbWalkerModule
        Implements IBotModule

        Public Const ModuleName As String = "Orb Walker"

        ' Configuration, swapped whole under _gate by Configure and read once per run, so a run
        ' never mixes the keys of two edits.
        Private ReadOnly _gate As New Object()
        Private _input As InputManager
        Private _master As Keys = Keys.None
        Private _hold As Keys = Keys.None
        Private _press As Keys = Keys.None
        Private _delayMs As Integer = 100
        Private _clickRight As Boolean = True
        Private _clickDelayMs As Integer = 100

        ''' <summary>
        ''' Whether the master key toggles the run (press to start, press again to stop) rather than
        ''' holding it. Read by the hook's edges, which arrive on the UI thread like Configure does.
        ''' </summary>
        Private _toggle As Boolean

        ' Whether a run is wanted, as two events rather than one flag, because the tick waits on each
        ' of them in turn: for the start while idle, for the stop while it waits a delay. In hold mode
        ' they follow the master key down and up; in toggle mode each press flips them.
        Private ReadOnly _start As New ManualResetEventSlim(False)
        Private ReadOnly _stop As New ManualResetEventSlim(True)

        Public ReadOnly Property Name As String Implements IBotModule.Name
            Get
                Return ModuleName
            End Get
        End Property

        ''' <summary>
        ''' Supplies the keys, the delays, the mode and a fresh input manager. Safe while running: the
        ''' run in progress keeps what it started with, and the next one reads this — except a change
        ''' of mode, which ends the run in progress. A run toggled on would otherwise go on under hold
        ''' mode with no key held down to let go of.
        ''' </summary>
        Public Sub Configure(input As InputConfig, cfg As OrbWalkerConfig)
            SyncLock _gate
                _input = InputManager.FromConfig(input)
                _master = CType(cfg.MasterKey, Keys)
                _hold = CType(cfg.HoldKey, Keys)
                _press = CType(cfg.PressKey, Keys)
                _delayMs = cfg.DelayMs
                _clickRight = cfg.ClickRight
                _clickDelayMs = cfg.ClickDelayMs
            End SyncLock
            If _toggle <> cfg.Toggle Then
                _toggle = cfg.Toggle
                StopRun()
            End If
        End Sub

        ''' <summary>
        ''' The master key went down. Called from the hook, which must return at once. Holding: the
        ''' run starts. Toggle: the run starts, or stops if it was going.
        ''' </summary>
        Public Sub MasterPressed()
            If _toggle AndAlso _start.IsSet Then StopRun() Else StartRun()
        End Sub

        ''' <summary>
        ''' The master key came up. Called from the hook, which must return at once. Holding: the run
        ''' ends. Toggle: nothing — the next press is what ends it.
        ''' </summary>
        Public Sub MasterReleased()
            If Not _toggle Then StopRun()
        End Sub

        Private Sub StartRun()
            _stop.Reset()
            _start.Set()
        End Sub

        Private Sub StopRun()
            _start.Reset()
            _stop.Set()
        End Sub

        Public Sub Initialize(context As BotContext) Implements IBotModule.Initialize
            ' Nothing to set up: Configure ran before the start, and no run is wanted yet —
            ' Shutdown leaves it that way, and the hook is only installed while this runs.
        End Sub

        Public Sub Tick(ct As CancellationToken) Implements IBotModule.Tick
            _start.Wait(ct)

            Dim input As InputManager, master, hold, press As Keys
            Dim delayMs, clickDelayMs As Integer, clickRight As Boolean
            SyncLock _gate
                input = _input
                master = _master
                hold = _hold
                press = _press
                delayMs = _delayMs
                clickRight = _clickRight
                clickDelayMs = _clickDelayMs
            End SyncLock

            ' How the run ends, in words, for the log: letting go of the key, or pressing it again.
            Dim ended As String = If(_toggle, $"{master} pressed again", $"{master} up")

            If input Is Nothing Then
                Logger.Warn(ModuleName, "Not configured — nothing is pressed.")
                _stop.Wait(ct)
                Return
            End If

            ' One run of the cycle. Waits here are as long as they say, not rounded up to the
            ' scheduler's 15.6 ms — see PrecisionTimerScope.
            Using New PrecisionTimerScope()
                If Not input.KeyDown(hold) Then
                    ' The board refused (ArduinoLink has logged why). Nothing else is pressed until
                    ' the run is ended — a cycle without its held key is not the one asked for.
                    Logger.Warn(ModuleName, $"Could not hold {hold} — this run is skipped")
                    _stop.Wait(ct)
                    Return
                End If

                Logger.Info(ModuleName, If(_toggle,
                                           $"{master} pressed — holding {hold} until it is pressed again",
                                           $"{master} down — holding {hold}"))
                Dim cycles As Integer = 0
                ' Every step is traced with the time it actually took, so the order and the gaps can
                ' be read off the Log (with Trace on) rather than guessed from the game.
                Dim sinceClick As Stopwatch = Nothing
                Try
                    While Not _stop.IsSet
                        ct.ThrowIfCancellationRequested()
                        Dim pressed As Boolean = input.PressKey(press)
                        Dim sincePress As Stopwatch = Stopwatch.StartNew()
                        Logger.Trace(ModuleName,
                                     $"Pressed {press}" &
                                     If(sinceClick Is Nothing, " — first of the run", $" — {sinceClick.ElapsedMilliseconds} ms after the click") &
                                     If(pressed, "", " — the board did not take it"))

                        If _stop.Wait(delayMs, ct) Then
                            Logger.Trace(ModuleName, $"{ended} {sincePress.ElapsedMilliseconds} ms into the delay after the press — no click")
                            Exit While
                        End If

                        Dim waited As Long = sincePress.ElapsedMilliseconds
                        Dim clicked As Boolean = input.ClickAtCursor(clickRight)
                        sinceClick = Stopwatch.StartNew()
                        Logger.Trace(ModuleName,
                                     $"{If(clickRight, "Right", "Left")} click at the cursor — {waited} ms after pressing {press}" &
                                     If(clicked, "", " — the board did not take it"))
                        cycles += 1

                        If _stop.Wait(clickDelayMs, ct) Then
                            Logger.Trace(ModuleName, $"{ended} {sinceClick.ElapsedMilliseconds} ms into the delay after the click")
                            Exit While
                        End If
                    End While
                Finally
                    input.KeyUp(hold)
                    Logger.Info(ModuleName, $"{ended} — released {hold} after {cycles} " &
                                            If(cycles = 1, "cycle", "cycles"))
                End Try
            End Using
        End Sub

        Public Sub Shutdown() Implements IBotModule.Shutdown
            ' The run's own Finally has already let go of the hold key. This leaves the next start
            ' waiting for a fresh press, in either mode — a run toggled on is not carried over a
            ' switch off and on.
            StopRun()
        End Sub

    End Class

End Namespace
