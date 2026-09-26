Imports System.Drawing
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports wklol.Core

' ══════════════════════════════════════════════════════════════════════════════
' FrmShell  —  the bot's window.
'
' It holds a browser and nothing else. Every control the user sees is HTML in
' web\, every question it answers comes back over WebBridge, and this class owns
' only what a WinForms form has to own: the window, the native dialogs that must
' be native, and the lifetime of the pieces behind them.
'
' THE BOT ITSELF IS BotHost. The module, the engine and the master key's hook
' belong to it, not to any window; this form builds it before it is shown and
' ends it as the process ends.
'
' CLOSING IS A QUESTION, AND THEN IT IS FINAL. The X asks first — in the page,
' in the interface's own words — because closing stops a module that may be in
' the middle of a run. Confirmed, the bot lets go of what must not outlive it
' (the board's keys, the keyboard hook, the log) against a deadline, and the
' process is killed: nothing it left behind can keep it running with no window.
' ══════════════════════════════════════════════════════════════════════════════

Public Class FrmShell
    Inherits Form

    Private ReadOnly _store As New SettingsStore()
    Private _bridge As WebBridge
    Private _host As BotHost

    ''' <summary>Compact on purpose — screen the bot takes is screen the game does not have.</summary>
    Private Shared ReadOnly StartSize As New Size(1040, 680)

    Public Sub New()
        Text = "WK LoL"
        ClientSize = StartSize
        MinimumSize = New Size(900, 600)
        StartPosition = FormStartPosition.CenterScreen
        DoubleBuffered = True

        ' The page paints its own background within a frame or two; this is what shows in the gap,
        ' and it matches the interface's own surface so the open does not flash white.
        BackColor = Color.FromArgb(242, 245, 250)

        Try
            Icon = Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        Catch
            ' A missing icon is not a reason to fail to open.
        End Try
    End Sub

    ''' <summary>
    ''' Everything this window needs is done here, before it is shown. The browser is started
    ''' first — WebView2 runs in processes of its own, so it comes up while the bot is starting
    ''' rather than after it — and the bot is started before MyBase.OnLoad, which is where WinForms
    ''' posts the Shown event.
    ''' </summary>
    Protected Overrides Sub OnLoad(e As EventArgs)
        _bridge = New WebBridge(_store)
        WireBridge()

        ' Not awaited: the browser finishes starting on its own, and the page shows its own
        ' skeletons until the first reply lands.
        Dim ignored = WebShell.AttachAsync(Me, _bridge)

        StartBot()
        MyBase.OnLoad(e)
    End Sub

    ''' <summary>
    ''' Builds the bot and starts it. Guarded: a start-up that throws still leaves a window that
    ''' opens, says so in the log, and can be closed.
    ''' </summary>
    Private Sub StartBot()
        Try
            _host = New BotHost(_store)
            _host.Start()
        Catch ex As Exception
            Logger.Error("Shell", "The bot could not be started", ex)
        End Try
    End Sub

    ''' <summary>The bot, once it has started — Nothing when the start-up failed.</summary>
    Private Function StartedHost() As BotHost
        If _host Is Nothing OrElse Not _host.IsStarted Then Return Nothing
        Return _host
    End Function

    ' ── Bridge hooks ─────────────────────────────────────────────────────────
    ' Everything here is something a web page cannot do: drive a modal dialog,
    ' touch the clipboard or the disk, or own the engine's lifetime.

    Private Sub WireBridge()
        _bridge.Host = AddressOf StartedHost
        _bridge.CaptureKey = AddressOf CaptureKey
        _bridge.CopyText = AddressOf CopyTextToClipboard
        _bridge.SaveText = AddressOf SaveTextToFile
        _bridge.ShowLogFile = AddressOf ShowLogFile
        _bridge.CloseShown = Sub() _closeShown = True
        _bridge.ExitApplication = Sub() BeginInvoke(New Action(Sub() Terminate("closed by the user")))
    End Sub

    ''' <summary>
    ''' The key-capture dialog. Returns the key pressed, or Keys.None when cancelled.
    '''
    ''' THE YIELD IS NOT OPTIONAL. Every hook on this class runs inside WebView2's
    ''' WebMessageReceived handler: the page posts, the runtime calls us, and we are on its stack.
    ''' Entering a modal loop there lets the pushes a running module makes post back into WebView2
    ''' before the callback that opened the dialog has returned — reentrancy that takes the process
    ''' down with no managed exception. Awaiting Task.Yield posts the rest of this method back to
    ''' the message queue, so the dialog opens on a later turn of the loop with nothing underneath.
    ''' </summary>
    Private Async Function CaptureKey() As Task(Of Keys)
        Await Task.Yield()
        Try
            Using capture As New FrmKeyCapture()
                If capture.ShowDialog(Me) <> DialogResult.OK Then Return Keys.None
                Return capture.CapturedKey
            End Using
        Catch ex As Exception
            Logger.Warn("Shell", "Key capture failed: " & ex.Message)
            Return Keys.None
        End Try
    End Function

    ''' <summary>
    ''' Puts text on the clipboard. False when another program is holding it: Clipboard.SetText
    ''' throws in that case, and a copy that did not happen is worth saying so about.
    ''' </summary>
    Private Function CopyTextToClipboard(text As String) As Boolean
        If String.IsNullOrEmpty(text) Then Return False
        Try
            Clipboard.SetText(text)
            Return True
        Catch ex As Exception
            Logger.Warn("Shell", "Could not write to the clipboard: " & ex.Message)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Saves the console's text wherever the user says. Yields first for the reason
    ''' <see cref="CaptureKey"/> spells out: SaveFileDialog must not open on WebView2's stack.
    ''' </summary>
    Private Async Function SaveTextToFile(suggestedName As String, text As String) As Task(Of String)
        Await Task.Yield()

        Try
            Using dlg As New SaveFileDialog() With {
                .Title = "Save the console",
                .FileName = suggestedName,
                .DefaultExt = "log",
                .Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                .InitialDirectory = LogFolder()}

                If dlg.ShowDialog(Me) <> DialogResult.OK Then Return "Cancelled — nothing was saved."

                ' UTF-8 without a BOM, like the session file: the two get opened by the same
                ' editors and should not differ in how they open.
                IO.File.WriteAllText(dlg.FileName, text, New System.Text.UTF8Encoding(False))
                Logger.Info("Log", $"Console saved to {dlg.FileName}")
                Return $"Saved to {IO.Path.GetFileName(dlg.FileName)}."
            End Using
        Catch ex As Exception
            Logger.Warn("Shell", "Could not save the console: " & ex.Message)
            Return "Could not save it: " & ex.Message
        End Try
    End Function

    ''' <summary>
    ''' Opens Explorer on this session's own log file, selected. Without a file there is still a
    ''' folder worth opening.
    ''' </summary>
    Private Function ShowLogFile() As Task(Of String)
        Try
            Dim path As String = Logger.FilePath
            If path IsNot Nothing AndAlso IO.File.Exists(path) Then
                Process.Start("explorer.exe", $"/select,""{path}""")
                Return Task.FromResult(Of String)(Nothing)
            End If

            Dim folder = LogFolder()
            IO.Directory.CreateDirectory(folder)
            Process.Start("explorer.exe", $"""{folder}""")
            Return Task.FromResult("No file was opened for this session — here is the folder.")
        Catch ex As Exception
            Logger.Warn("Shell", "Could not open the log folder: " & ex.Message)
            Return Task.FromResult("Could not open the log folder: " & ex.Message)
        End Try
    End Function

    ''' <summary>Where the session logs are written — the same folder BotHost hands to the logger.</summary>
    Private Shared Function LogFolder() As String
        Dim path As String = Logger.FilePath
        If Not String.IsNullOrEmpty(path) Then
            Try
                Return IO.Path.GetDirectoryName(path)
            Catch
                ' A path that cannot be taken apart is not worth failing the click over.
            End Try
        End If
        Return IO.Path.Combine(Application.StartupPath, "logs")
    End Function

    ' ══════════════════════════════════════════════════════════════════════════
    ' CLOSING
    ' ══════════════════════════════════════════════════════════════════════════

    ''' <summary>
    ''' How long the page gets to say it is showing the close question. Past this the page is not
    ''' answering — a script fault, a browser that never started — and the question is asked
    ''' natively instead: a window that cannot be closed is worse than a plain dialog.
    ''' </summary>
    Private Const CloseAnswerMs As Integer = 1500

    ''' <summary>
    ''' How long the teardown gets before the process is killed regardless. The steps inside it
    ''' each have short timeouts of their own; this is the ceiling for all of them together.
    ''' </summary>
    Private Const ShutdownDeadlineMs As Integer = 2500

    ''' <summary>Set by the page, through the bridge, once the close question is on screen.</summary>
    Private _closeShown As Boolean

    ''' <summary>The wait for <see cref="_closeShown"/>; Nothing while no question is out.</summary>
    Private _closeWatch As System.Windows.Forms.Timer

    Private _terminating As Boolean

    ''' <summary>
    ''' The X — and Alt+F4, and the taskbar's Close — ask before they close. Anything else that
    ''' closes this window is Windows ending the session or the process being told to stop, and
    ''' neither is a question: the bot shuts down at once.
    ''' </summary>
    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If _terminating Then
            MyBase.OnFormClosing(e)
            Return
        End If

        e.Cancel = True
        If e.CloseReason = CloseReason.UserClosing Then
            AskToClose()
        Else
            Terminate($"the window was closed ({e.CloseReason})")
        End If
    End Sub

    ''' <summary>
    ''' Puts the close question in front of the user. In the page when there is a page that
    ''' answers, and natively otherwise.
    ''' </summary>
    Private Sub AskToClose()
        ' A window closed from the taskbar while minimised would ask a question nobody can see.
        If WindowState = FormWindowState.Minimized Then WindowState = FormWindowState.Normal
        Activate()

        If _bridge Is Nothing OrElse Not _bridge.CanPush Then
            AskToCloseNatively()
            Return
        End If

        _closeShown = False
        Try
            _bridge.Push("app.close", _bridge.DescribeClose())
        Catch ex As Exception
            Logger.Warn("Shell", "Could not ask the page about closing: " & ex.Message)
            AskToCloseNatively()
            Return
        End Try

        If _closeWatch Is Nothing Then
            _closeWatch = New System.Windows.Forms.Timer() With {.Interval = CloseAnswerMs}
            AddHandler _closeWatch.Tick,
                Sub()
                    _closeWatch.Stop()
                    If _closeShown OrElse _terminating Then Return
                    Logger.Warn("Shell", "The page did not answer the close question — asking natively")
                    AskToCloseNatively()
                End Sub
        End If
        _closeWatch.Stop()
        _closeWatch.Start()
    End Sub

    ''' <summary>The same question, as a plain dialog, for a page that is not there to ask it.</summary>
    Private Sub AskToCloseNatively()
        Dim running = If(StartedHost()?.RunningModuleNames(), New String() {})
        Dim what As String = If(running.Length = 0, "Nothing is running.",
                                "Everything running stops: " & String.Join(", ", running) & ".")

        If MessageBox.Show(Me, "Close WK?" & vbCrLf & vbCrLf & what, "WK",
                           MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                           MessageBoxDefaultButton.Button2) = DialogResult.OK Then
            Terminate("closed by the user")
        End If
    End Sub

    ''' <summary>
    ''' Ends the bot and the process with it.
    '''
    ''' THE TEARDOWN IS KEPT, BUT IT HAS A DEADLINE. A key the board is holding and the global
    ''' keyboard hook are let go of properly, because killing past them would leave a key down with
    ''' nobody watching. But a watchdog kills the process after <see cref="ShutdownDeadlineMs"/>
    ''' whatever happens, and the teardown ends in the same kill when it finishes first.
    ''' </summary>
    Private Sub Terminate(reason As String)
        If _terminating Then Return
        _terminating = True
        _closeWatch?.Stop()
        Logger.Info("Shell", $"Closing — {reason}")

        Dim watchdog As New Threading.Thread(
            Sub()
                Threading.Thread.Sleep(ShutdownDeadlineMs)
                KillProcess()
            End Sub) With {.IsBackground = True, .Name = "Shutdown watchdog"}
        watchdog.Start()

        ' Off the screen at once: the click has been answered, and whatever the teardown takes
        ' is not something to watch.
        Try
            Hide()
        Catch
        End Try

        ' The bridge holds a subscription to the static logger; before the bot, which closes the
        ' log last.
        Try
            _bridge?.Detach()
        Catch ex As Exception
            Logger.Warn("Shell", "Could not detach the bridge: " & ex.Message)
        End Try
        Try
            _host?.Dispose()
        Catch
        End Try

        KillProcess()
    End Sub

    Private Shared Sub KillProcess()
        Try
            Process.GetCurrentProcess().Kill()
        Catch
            Environment.Exit(0)
        End Try
    End Sub

End Class
