Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports wklol.Core
Imports wklol.Modules

' ══════════════════════════════════════════════════════════════════════════════
' BotHost  —  what runs the bot, with no window of its own.
'
' It owns the module and its registration with BotEngine, the switch that starts
' or stops it, and the global hook that watches the master key. FrmShell builds
' one before it is shown and lets it go as it closes; WebBridge reaches it
' through the shell.
'
' A SWITCH IS A QUESTION, NOT A CHECKBOX. A module is switched by asking, and the
' answer says what it ended up as and, when it refused, why — in a sentence the
' page can show beside the switch.
' ══════════════════════════════════════════════════════════════════════════════

''' <summary>What a module switch ended up as, and why when that is not what was asked.</summary>
Public NotInheritable Class ModuleSwitchResult

    ''' <summary>Whether the module is on AFTERWARDS — never whether the ask was honoured.</summary>
    Public Property Running As Boolean

    ''' <summary>Why starting it was refused, in a sentence for the page. Nothing when it was not.</summary>
    Public Property Refusal As String

End Class

Public NotInheritable Class BotHost
    Implements IDisposable

    Private ReadOnly _store As SettingsStore

    Private _orbWalker As OrbWalkerModule
    Private _orbWalkerOn As Boolean
    Private _switching As Boolean

    ''' <summary>
    ''' The master key's watcher. Installed only while the Orb Walker is on: the hook swallows the
    ''' key it is bound to, and while the module is off that key belongs to the game.
    ''' </summary>
    Private _hook As GlobalKeyHook

    Private _started As Boolean
    Private _disposed As Boolean

    Public Sub New(store As SettingsStore)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        _store = store
    End Sub

    ''' <summary>Whether <see cref="Start"/> has run, so a caller knows the state means anything.</summary>
    Public ReadOnly Property IsStarted As Boolean
        Get
            Return _started
        End Get
    End Property

    ' ══════════════════════════════════════════════════════════════════════════
    ' START-UP
    ' ══════════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Brings the bot up: the session log, then the module. Each step is guarded on its own, so
    ''' one that throws is reported and does not silently skip the rest.
    '''
    ''' The module is NOT switched on here. It starts off every session, and comes on when the
    ''' user asks for it.
    ''' </summary>
    Public Sub Start()
        If _started OrElse _disposed Then Return
        _started = True

        ' Before the first step logs anything: the interesting lines are routinely the early ones.
        Logger.OpenFile(Path.Combine(Application.StartupPath, "logs"))
        If Logger.FilePath IsNot Nothing Then Logger.Info("Startup", $"Session log: {Logger.FilePath}")

        RunStep("engine init", AddressOf RegisterModules)
    End Sub

    Private Sub RegisterModules()
        _orbWalker = New OrbWalkerModule()
        BotEngine.Instance.Register(_orbWalker)

        _hook = New GlobalKeyHook()
        AddHandler _hook.HotkeyPressed, Sub() _orbWalker.MasterPressed()
        AddHandler _hook.HotkeyReleased, Sub() _orbWalker.MasterReleased()
    End Sub

    ''' <summary>Runs one step, reporting a failure instead of hiding it.</summary>
    Private Shared Sub RunStep(name As String, step_ As Action)
        Try
            step_()
        Catch ex As Exception
            Logger.Error("Startup", $"Step '{name}' failed — the rest carries on", ex)
        End Try
    End Sub

    ' ══════════════════════════════════════════════════════════════════════════
    ' THE SWITCH
    ' ══════════════════════════════════════════════════════════════════════════

    ''' <summary>Whether the Orb Walker is on right now.</summary>
    Public ReadOnly Property OrbWalkerOn As Boolean
        Get
            Return _orbWalkerOn
        End Get
    End Property

    ''' <summary>Every module actually running, by the name the page shows.</summary>
    Public Function RunningModuleNames() As String()
        Return If(_orbWalkerOn, New String() {OrbWalkerModule.ModuleName}, New String() {})
    End Function

    ''' <summary>
    ''' Switches the Orb Walker on or off. Starting it asks everything first — the settings, then
    ''' the board itself — and only then installs the hook and starts the thread; the answer is
    ''' what it ended up as, with the reason when it refused.
    '''
    ''' Called on the UI thread, and it returns there after the one wait it has: the hook has to be
    ''' installed from a thread with a message loop.
    ''' </summary>
    Public Async Function SetOrbWalkerAsync(on_ As Boolean) As Task(Of ModuleSwitchResult)
        If on_ = _orbWalkerOn Then Return New ModuleSwitchResult With {.Running = _orbWalkerOn}
        If _switching Then
            Return New ModuleSwitchResult With {.Running = _orbWalkerOn,
                                                .Refusal = "The last switch is still being made — try again in a moment."}
        End If

        If Not on_ Then
            StopOrbWalker()
            Return New ModuleSwitchResult With {.Running = False}
        End If

        _switching = True
        Try
            Dim refusal As String = Await StartOrbWalkerAsync()
            If refusal IsNot Nothing Then Logger.Warn(OrbWalkerModule.ModuleName, "Not started — " & refusal)
            Return New ModuleSwitchResult With {.Running = _orbWalkerOn, .Refusal = refusal}
        Catch ex As Exception
            Logger.Error(OrbWalkerModule.ModuleName, "Switching on failed", ex)
            Return New ModuleSwitchResult With {.Running = _orbWalkerOn,
                                                .Refusal = "The Orb Walker could not be switched on — " & ex.Message}
        Finally
            _switching = False
        End Try
    End Function

    ''' <summary>Starts the module, or says why not. Nothing when it started.</summary>
    Private Async Function StartOrbWalkerAsync() As Task(Of String)
        If _orbWalker Is Nothing OrElse _hook Is Nothing Then
            Return "The bot engine did not start. Restart the application."
        End If

        ' The settings first — the same list the screen shows.
        Dim problems = OrbWalkerSetup.Problems(_store.Settings)
        If problems.Count > 0 Then Return problems(0)

        ' Then the board itself. The settings say it WAS ready; a switch that turned on over a board
        ' that is not plugged in would be a module that silently presses nothing.
        Dim link As ArduinoLink = ArduinoLink.Instance
        link.PreferredPort = _store.Settings.Input.ArduinoPort
        link.RetryNow()
        Dim connected As Boolean = Await Task.Run(Function() link.EnsureConnected())
        If Not connected Then
            Return $"The board is not answering — {If(link.LastError, "it is not connected")}. Check it in Setup."
        End If
        If link.FirmwareVersion < ArduinoLink.CurrentFirmware Then
            Return $"The board runs wk_hid v{link.FirmwareVersion}; clicking where the cursor is takes " &
                   $"v{ArduinoLink.CurrentFirmware}. Flash it in Setup → Firmware."
        End If
        If _disposed Then Return "The bot is closing."

        ConfigureOrbWalker()
        If Not _hook.Install() Then
            Return "Windows refused the keyboard hook, so the master key cannot be watched."
        End If
        Dim master = CType(_store.Settings.OrbWalker.MasterKey, Keys)
        _hook.SetBinding(master)

        BotEngine.Instance.StartModule(OrbWalkerModule.ModuleName)
        _orbWalkerOn = True
        Logger.Info(OrbWalkerModule.ModuleName, $"Module enabled — hold {master} to walk")
        Return Nothing
    End Function

    Private Sub StopOrbWalker()
        ' The hook first: the master key goes back to the game at once. A run in progress — held or
        ' toggled on — then ends when the module is stopped below, and its Finally lets go of the
        ' hold key.
        Try
            _hook?.Uninstall()
            _hook?.SetBinding(Keys.None)
        Catch ex As Exception
            Logger.Warn(OrbWalkerModule.ModuleName, "Could not remove the keyboard hook: " & ex.Message)
        End Try

        BotEngine.Instance.StopModule(OrbWalkerModule.ModuleName)

        ' The run's own Finally has let go of the hold key. This is for the one that did not get
        ' there inside the join: a key left down is the one state that must never outlive a switch.
        ArduinoLink.Instance.ReleaseAll()

        _orbWalkerOn = False
        Logger.Info(OrbWalkerModule.ModuleName, "Module disabled")
    End Sub

    Private Sub ConfigureOrbWalker()
        _orbWalker.Configure(_store.Settings.Input, _store.Settings.OrbWalker)
    End Sub

    ''' <summary>
    ''' Re-pushes the settings to the module while it runs, so an edit on any screen — a key, the
    ''' delay, a hold time — applies without a stop and start. The run in progress keeps what it
    ''' started with; the next one reads the edit.
    ''' </summary>
    Public Sub ReconfigureRunning()
        If Not _orbWalkerOn Then Return
        ConfigureOrbWalker()
        _hook.SetBinding(CType(_store.Settings.OrbWalker.MasterKey, Keys))
    End Sub

    ' ══════════════════════════════════════════════════════════════════════════
    ' SHUTDOWN
    ' ══════════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' Lets go of everything that must not outlive the bot, in the order that matters: the hook,
    ''' so the master key is the game's again; the module; then the board, so no key it is holding
    ''' outlives the module that pressed it; the log last.
    '''
    ''' Safe to call twice, and every step is guarded because the next one matters more than the
    ''' reason this one failed.
    ''' </summary>
    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True

        Try
            _hook?.Dispose()
        Catch
        End Try
        Try
            ' A short join: the close must not sit waiting for a run mid-wait.
            BotEngine.Instance.StopAll(joinTimeoutMs:=500)
        Catch
        End Try
        Try
            ArduinoLink.Instance.Dispose()
        Catch
        End Try
        Logger.CloseFile()
    End Sub

End Class
