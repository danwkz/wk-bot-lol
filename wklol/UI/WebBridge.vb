Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

' ══════════════════════════════════════════════════════════════════════════════
' WebBridge  —  the only channel between the HTML interface and the bot.
'
' PROTOCOL. Three message shapes, and nothing else crosses:
'
'   request   { id, cmd, data }    page   → bot
'   reply     { id, ok, data }     bot    → page,  answering one request
'             { id, ok:false, error }
'   event     { evt, data }        bot    → page,  unsolicited
'
' EVERY REQUEST IS ANSWERED. A handler that throws becomes ok:false with the
' message; a command nobody registered becomes ok:false too. The page puts a
' spinner on the control that made the call and takes it off when the reply
' lands, so a request that silently went nowhere is a control that spins until
' the page's own timeout fires.
'
' THREADING. WebView2 raises WebMessageReceived on the UI thread and only the UI
' thread may post back. Handlers therefore run on the UI thread, and anything
' slow — a serial port, a WMI query, an upload — says so by awaiting a Task.
'
' NO BOT LOGIC LIVES IN THE PAGE. Every question the interface asks — is setup
' complete, may the module be switched on, what is wrong with its keys — is
' answered here, from the same objects the engine reads. The page renders it.
' ══════════════════════════════════════════════════════════════════════════════

Public NotInheritable Class WebBridge

    Private ReadOnly _store As SettingsStore
    Private ReadOnly _setup As ArduinoSetup
    Private ReadOnly _handlers As New Dictionary(Of String, Func(Of JObject, Task(Of Object)))(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' The Log screen's live half: it subscribes to the logger and pushes batches at the page.
    ''' Built with the bridge rather than on demand, so the console has the session behind it the
    ''' first time it is opened. See <see cref="LogFeed"/>.
    ''' </summary>
    Private ReadOnly _log As LogFeed

    ''' <summary>Set by <see cref="WebShell"/>. Posts a JSON string to the page, on the UI thread.</summary>
    Private _post As Action(Of String)

    ''' <summary>Whether there is a page to push to at all — False when the browser never started.</summary>
    Public ReadOnly Property CanPush As Boolean
        Get
            Return _post IsNot Nothing
        End Get
    End Property

    ' ── Hooks the host form fills in ──────────────────────────────────────────
    ' The bridge deliberately knows nothing about forms. Anything that has to
    ' open a native window is handed in by FrmShell, and a hook left unset
    ' degrades to an honest "not available" instead of a crash.

    ''' <summary>The bot, once it has started — Nothing when its start-up failed.</summary>
    Public Property Host As Func(Of BotHost)

    ''' <summary>
    ''' Opens the native key-capture dialog and returns the key pressed, or Keys.None when it was
    ''' cancelled. Native because it has to see keys a web page never will: the F-keys the browser
    ''' keeps for itself, and keys it swallows before any script runs.
    ''' </summary>
    Public Property CaptureKey As Func(Of Task(Of Keys))

    ''' <summary>Puts text on the clipboard. False when another program is holding it.</summary>
    Public Property CopyText As Func(Of String, Boolean)

    ''' <summary>Writes text to a file the user picks. Takes the suggested name and the text;
    ''' returns the message to toast.</summary>
    Public Property SaveText As Func(Of String, String, Task(Of String))

    ''' <summary>Shows this session's log file in Explorer. Returns the message to toast.</summary>
    Public Property ShowLogFile As Func(Of Task(Of String))

    ''' <summary>
    ''' The page has put the close confirmation on screen. The shell waits for this before it
    ''' trusts the page to be the thing asking — a page that cannot answer must not leave the
    ''' window impossible to close.
    ''' </summary>
    Public Property CloseShown As Action

    ''' <summary>The user confirmed closing the bot. Does not return: the process ends.</summary>
    Public Property ExitApplication As Action

    Public Sub New(store As SettingsStore)
        If store Is Nothing Then Throw New ArgumentNullException(NameOf(store))
        _store = store
        _setup = New ArduinoSetup(store)

        ' Subscribed from the moment the bridge exists, which is before the browser does. Lines
        ' written in that window are pushed into a page that is not listening and dropped — and
        ' they are not lost, because the console fills itself from the ring when it opens.
        _log = New LogFeed(AddressOf Push)

        RegisterHandlers()
    End Sub

    ''' <summary>
    ''' Lets go of everything that outlives the window. Logger is static, so the feed's
    ''' subscription would otherwise keep this bridge — and the form behind its hooks — alive.
    ''' </summary>
    Public Sub Detach()
        Try
            _log.Dispose()
        Catch ex As Exception
            Core.Logger.Warn("WebBridge", "Could not detach the log feed: " & ex.Message)
        End Try
        _post = Nothing
    End Sub

    ''' <summary>Called by <see cref="WebShell"/> once the page can receive messages.</summary>
    Friend Sub AttachPoster(post As Action(Of String))
        _post = post
    End Sub

    ' ── Inbound ───────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Handles one message from the page. Async void by necessity — it is driven by an event —
    ''' so every path out of it is wrapped: an exception escaping here would reach the WinForms
    ''' message loop and take the window down.
    ''' </summary>
    Friend Async Sub HandleMessage(json As String)
        Dim id As JToken = Nothing
        Try
            Dim msg = JObject.Parse(json)
            id = msg("id")
            Dim cmd = If(msg("cmd")?.ToString(), String.Empty)
            Dim data = TryCast(msg("data"), JObject)

            Dim handler As Func(Of JObject, Task(Of Object)) = Nothing
            If Not _handlers.TryGetValue(cmd, handler) Then
                Reply(id, False, Nothing, $"Unknown command: {cmd}")
                Return
            End If

            ' THE GATE IS ENFORCED HERE, NOT ONLY IN THE PAGE. The page shows only the setup flow
            ' until the board is ready, but that is chrome — a page that lied about
            ' `setup.complete` could still ask for anything. Only the flow's own commands (and
            ' closing) answer before Setup is finished; everything else is refused with the same
            ' blocker the flow is already showing.
            If Not IsAllowedDuringSetup(cmd) AndAlso Not _setup.IsComplete Then
                Reply(id, False, Nothing, "Finish Setup before using the bot.")
                Return
            End If

            Reply(id, True, Await handler(If(data, New JObject())), Nothing)

        Catch ex As Exception
            Core.Logger.Warn("WebBridge", $"Request failed: {ex.Message}")
            Reply(id, False, Nothing, ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' The only commands the page may ask for before Setup is finished: the bootstrap that draws
    ''' it, everything under "setup.", and closing — a window that cannot be closed until setup is
    ''' finished is a window held hostage by it.
    ''' </summary>
    Private Shared Function IsAllowedDuringSetup(cmd As String) As Boolean
        Return cmd.StartsWith("setup.", StringComparison.OrdinalIgnoreCase) OrElse
               cmd.StartsWith("app.", StringComparison.OrdinalIgnoreCase)
    End Function

    ' ── Outbound ──────────────────────────────────────────────────────────────

    Private Sub Reply(id As JToken, ok As Boolean, data As Object, [error] As String)
        If _post Is Nothing Then Return
        Dim payload As New JObject()
        payload("id") = If(id, JValue.CreateNull())
        payload("ok") = ok
        If ok Then payload("data") = If(data Is Nothing, JValue.CreateNull(), JToken.FromObject(data))
        If Not ok Then payload("error") = [error]
        _post(payload.ToString(Formatting.None))
    End Sub

    ''' <summary>
    ''' Pushes an unsolicited event to the page — engine state, a toast, a log batch. Safe to call
    ''' before the page exists; it is dropped rather than queued, because a status line that
    ''' arrives late is worse than one that never arrived.
    ''' </summary>
    Public Sub Push(evt As String, data As Object)
        If _post Is Nothing Then Return
        Dim payload As New JObject()
        payload("evt") = evt
        payload("data") = If(data Is Nothing, JValue.CreateNull(), JToken.FromObject(data))
        _post(payload.ToString(Formatting.None))
    End Sub

    ' ── Handlers ──────────────────────────────────────────────────────────────

    Private Sub RegisterHandlers()

        ' One call fills the whole shell. It means the page has either all of its state or none of
        ' it — never a half-populated screen.
        _handlers("app.bootstrap") = Function(data)
                                         Return Task.FromResult(Of Object)(New With {
                                             .build = BuildTag(),
                                             .setup = _setup.Describe(),
                                             .engine = DescribeEngine(),
                                             .orb = DescribeOrbWalker()
                                         })
                                     End Function

        RegisterSetupHandlers()
        RegisterOrbWalkerHandlers()
        RegisterSettingsHandlers()
        RegisterLogHandlers()
        RegisterAppHandlers()
    End Sub

    ' ── Setup ──────────────────────────────────────────────────────────────────
    ' Every command answers with the whole flow again, so nothing on the page can disagree with
    ' what the bot found. The slow ones are awaited off the UI thread inside ArduinoSetup.

    Private Sub RegisterSetupHandlers()

        _handlers("setup.state") = Function(data)
                                       Return Task.FromResult(Of Object)(_setup.Describe())
                                   End Function

        _handlers("setup.scan") = Async Function(data)
                                      Await _setup.ScanAsync()
                                      Return _setup.Describe()
                                  End Function

        _handlers("setup.port") = Async Function(data)
                                      Await _setup.SelectPortAsync(SetupFields.TextOf(data, "port"))
                                      Return _setup.Describe()
                                  End Function

        _handlers("setup.firmware") = Async Function(data)
                                          Await _setup.CheckFirmwareAsync()
                                          Return _setup.Describe()
                                      End Function

        ' The long one: a cold first run downloads the build tools. Every line the upload prints is
        ' pushed as it arrives, so the card narrates it rather than spinning for minutes.
        _handlers("setup.flash") = Async Function(data)
                                       Dim narrate As Action(Of String) =
                                           Sub(line) Push("setup.progress", New With {.text = line})
                                       Dim message As String
                                       Try
                                           message = Await _setup.FlashAsync(SetupFields.BoolOf(data, "confirmed"), narrate)
                                       Finally
                                           Push("setup.progress", New With {.text = CStr(Nothing)})
                                       End Try
                                       Return New With {.state = _setup.Describe(), .message = message}
                                   End Function

        _handlers("setup.check") = Async Function(data)
                                       Await _setup.CheckAsync()
                                       Return _setup.Describe()
                                   End Function

        _handlers("setup.finish") = Function(data)
                                        _setup.Finish()
                                        PushEngine()
                                        Return Task.FromResult(Of Object)(New With {
                                            .state = _setup.Describe(),
                                            .orb = DescribeOrbWalker(),
                                            .message = "Setup complete — the board is ready."})
                                    End Function
    End Sub

    ' ── Orb Walker ─────────────────────────────────────────────────────────────

    Private Sub RegisterOrbWalkerHandlers()

        _handlers("orbwalker.state") = Function(data)
                                           Return Task.FromResult(Of Object)(DescribeOrbWalker())
                                       End Function

        ' The key never travels through the page: the native dialog captures it and it is stored
        ' here, so there is no window in which it can be lost or swapped.
        _handlers("orbwalker.key") = Async Function(data)
                                         If CaptureKey Is Nothing Then
                                             Throw New InvalidOperationException("Key capture is not available here.")
                                         End If
                                         Dim slot = SetupFields.TextOf(data, "slot")
                                         If slot <> OrbWalkerSetup.SlotMaster AndAlso slot <> OrbWalkerSetup.SlotHold AndAlso
                                            slot <> OrbWalkerSetup.SlotPress Then
                                             Throw New ArgumentException($"Unknown key: {slot}")
                                         End If

                                         Dim captured = Await CaptureKey.Invoke()
                                         If captured = Keys.None Then
                                             ' Cancelled. The state still goes back so the row repaints
                                             ' from the settings rather than from whatever the page had.
                                             Return New With {.state = DescribeOrbWalker()}
                                         End If

                                         OrbWalkerSetup.SetKey(_store, slot, captured)
                                         Host?.Invoke()?.ReconfigureRunning()
                                         Return New With {.state = DescribeOrbWalker()}
                                     End Function

        _handlers("orbwalker.save") = Function(data)
                                          OrbWalkerSetup.Save(_store, data)
                                          Host?.Invoke()?.ReconfigureRunning()
                                          Return Task.FromResult(Of Object)(New With {.state = DescribeOrbWalker()})
                                      End Function

        ' The answer is what the module ENDED UP as, since switching it on checks everything and
        ' can refuse — and the refusal is the sentence the page shows beside the switch.
        _handlers("orbwalker.enabled") = Async Function(data)
                                             Dim host = RequireHost()
                                             Dim want = SetupFields.BoolOf(data, "enabled")
                                             Dim result = Await host.SetOrbWalkerAsync(want)
                                             PushEngine()
                                             Dim refused = result.Running <> want
                                             Return New With {
                                                 .state = DescribeOrbWalker(),
                                                 .message = If(refused, If(result.Refusal, "The Orb Walker could not be switched."), CStr(Nothing)),
                                                 .kind = If(refused, "warn", CStr(Nothing))}
                                         End Function
    End Sub

    ' ── Settings ───────────────────────────────────────────────────────────────

    Private Sub RegisterSettingsHandlers()

        _handlers("settings.state") = Function(data)
                                          Return Task.FromResult(Of Object)(SettingsSetup.Describe(_store))
                                      End Function

        _handlers("settings.input") = Function(data)
                                          SettingsSetup.SaveInput(_store, data)
                                          Host?.Invoke()?.ReconfigureRunning()
                                          Return Task.FromResult(Of Object)(New With {.state = SettingsSetup.Describe(_store)})
                                      End Function

        ' Answered off the UI thread: opening a serial port and waiting for the firmware to say
        ' hello takes long enough to be seen, and the page is showing a spinner for all of it.
        _handlers("settings.board") = Async Function(data)
                                          Dim link = ArduinoLink.Instance
                                          link.PreferredPort = _store.Settings.Input.ArduinoPort
                                          link.RetryNow()
                                          Dim ok = Await Task.Run(Function() link.EnsureConnected())
                                          Return New With {
                                              .connected = ok,
                                              .text = If(ok, link.DescribeStatus(),
                                                         "No board — nothing is being sent. " & link.DescribeStatus())}
                                      End Function
    End Sub

    ' ── Log ────────────────────────────────────────────────────────────────────
    ' The console reads the logger; it does not own it. Nothing here can lose a
    ' line: clearing hides, copying and saving read, and the session file goes on
    ' being written throughout. See LogFeed for the live half.

    Private Sub RegisterLogHandlers()

        _handlers("log.state") = Function(data)
                                     Return Task.FromResult(Of Object)(_log.Describe())
                                 End Function

        ' Turning the trace ON is also a backfill: the ring has been keeping the trace all along,
        ' so the reply carries the lines that were written while nobody was looking at them.
        _handlers("log.trace") = Function(data)
                                     _log.Trace = SetupFields.BoolOf(data, "on")
                                     Return Task.FromResult(Of Object)(_log.Describe())
                                 End Function

        _handlers("log.clear") = Function(data)
                                     _log.Clear()
                                     Return Task.FromResult(Of Object)(New With {
                                         .state = _log.Describe(),
                                         .message = "Console cleared — the session file still has every line."})
                                 End Function

        ' The page composes the text, because what is worth copying is what the filters left on
        ' screen, and this side has no idea what those are.
        _handlers("log.copy") = Function(data)
                                    Dim text = If(data("text")?.ToString(), String.Empty)
                                    If String.IsNullOrEmpty(text) Then
                                        Throw New InvalidOperationException("There is nothing on screen to copy.")
                                    End If
                                    If CopyText Is Nothing Then
                                        Throw New InvalidOperationException("The clipboard is not available here.")
                                    End If
                                    If Not CopyText.Invoke(text) Then
                                        Throw New InvalidOperationException(
                                            "Another program is holding the clipboard — try again.")
                                    End If

                                    Dim lines = Math.Max(0, SetupFields.IntOf(data, "lines"))
                                    Return Task.FromResult(Of Object)(New With {
                                        .message = If(lines = 1, "1 line copied.", $"{lines} lines copied.")})
                                End Function

        _handlers("log.save") = Async Function(data)
                                    Dim text = If(data("text")?.ToString(), String.Empty)
                                    If String.IsNullOrEmpty(text) Then
                                        Throw New InvalidOperationException("There is nothing on screen to save.")
                                    End If
                                    If SaveText Is Nothing Then
                                        Throw New InvalidOperationException("Saving is not available here.")
                                    End If

                                    Dim name = $"wklol-console-{DateTime.Now:yyyyMMdd-HHmmss}.log"
                                    Return New With {.message = Await SaveText.Invoke(name, text)}
                                End Function

        _handlers("log.open") = Async Function(data)
                                    If ShowLogFile Is Nothing Then
                                        Throw New InvalidOperationException("The log folder is not available here.")
                                    End If
                                    Return New With {.message = Await ShowLogFile.Invoke()}
                                End Function
    End Sub

    ' ── The application ────────────────────────────────────────────────────────
    ' Closing the bot. The shell asks the page to confirm (the "app.close" push),
    ' the page says it is showing the question, and the answer comes back here.

    Private Sub RegisterAppHandlers()
        _handlers("app.close.shown") = Function(data)
                                           CloseShown?.Invoke()
                                           Return Task.FromResult(Of Object)(Nothing)
                                       End Function

        ' Answered before the process ends, so the page's promise settles instead of timing out
        ' into a toast on a window that is on its way out. The exit itself is posted.
        _handlers("app.exit") = Function(data)
                                    If ExitApplication Is Nothing Then
                                        Throw New InvalidOperationException("Closing is not available here.")
                                    End If
                                    ExitApplication.Invoke()
                                    Return Task.FromResult(Of Object)(Nothing)
                                End Function
    End Sub

    ''' <summary>What the close confirmation needs to say: what stops.</summary>
    Public Function DescribeClose() As Object
        Dim running As String() = New String() {}
        Try
            running = If(Host?.Invoke()?.RunningModuleNames(), New String() {})
        Catch ex As Exception
            Core.Logger.Warn("WebBridge", "Could not list the running modules: " & ex.Message)
        End Try
        Return New With {.running = running}
    End Function

    ' ── Projections ───────────────────────────────────────────────────────────
    ' What the page renders. Each of these is the ONLY place a given shape is
    ' built, so the contract with app.js lives in one readable list.

    Private Function RequireHost() As BotHost
        Dim host = Me.Host?.Invoke()
        If host Is Nothing Then Throw New InvalidOperationException("The bot did not start — see the log.")
        Return host
    End Function

    Private Function DescribeOrbWalker() As Object
        Dim running = If(Host?.Invoke()?.OrbWalkerOn, False)
        Return OrbWalkerSetup.Describe(_store, running)
    End Function

    ''' <summary>
    ''' The line in the rail's footer: is anything running, and what. It reports the module, not
    ''' the settings — a switch that was refused is not running, whatever was asked for.
    ''' </summary>
    Private Function DescribeEngine() As Object
        Dim running As Boolean = False
        Dim text As String = "Idle"
        Try
            Dim names = If(Host?.Invoke()?.RunningModuleNames(), New String() {})
            running = names.Length > 0
            If running Then
                text = If(names.Length = 1, names(0) & " running", $"{names.Length} modules running")
            ElseIf Not _setup.IsComplete Then
                text = "Setup pending"
            End If
        Catch ex As Exception
            Core.Logger.Warn("WebBridge", "Could not describe the engine: " & ex.Message)
        End Try
        Return New With {.running = running, .text = text}
    End Function

    ''' <summary>Pushes the rail footer's line. Call when what is running may have changed.</summary>
    Public Sub PushEngine()
        Push("engine", DescribeEngine())
    End Sub

    ''' <summary>Short build marker for the sidebar — the assembly version.</summary>
    Private Shared Function BuildTag() As String
        Try
            Dim v = Reflection.Assembly.GetExecutingAssembly().GetName().Version
            If v IsNot Nothing Then Return $"{v.Major}.{v.Minor}"
        Catch
        End Try
        Return "dev"
    End Function

End Class
