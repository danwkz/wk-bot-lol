Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Web.WebView2.Core
Imports Microsoft.Web.WebView2.WinForms

' ══════════════════════════════════════════════════════════════════════════════
' WebShell  —  hosts the HTML interface inside a WinForms control.
'
' WHERE THE PAGE COMES FROM. Out of the executable itself (see WebAssets), through
' a scheme of our own: the page lives at wk://app/, and every request under it is
' answered from memory by ServeAsset. The page behaves like a normal site —
' relative paths, a secure origin, storage — with no port bound, no firewall
' prompt and nothing on disk to edit. A scheme rather than a fake https origin
' because Edge checks every https page against SmartScreen, and a name that
' exists nowhere cost wk ~1.7 s of blank window on every launch.
'
' THE PAGE STAYS IN ITS ORIGIN. It holds the only channel into the bot, so it
' may not navigate anywhere else, and a message that does not come from it is
' not read.
'
' WHAT CAN GO WRONG, AND WHAT HAPPENS THEN. The WebView2 runtime is part of
' Windows 11 and reaches Windows 10 through Edge, but it can be absent or
' broken. Every failure here ends in ShowFallback: a plain WinForms panel that
' says what is missing and where to get it. Closing still asks first, natively.
' ══════════════════════════════════════════════════════════════════════════════

Public NotInheritable Class WebShell

    Private Sub New()
    End Sub

    ''' <summary>The scheme the interface is served from. Registered with this process's browser only.</summary>
    Private Const AppScheme As String = "wk"

    ''' <summary>The page's origin, and the prefix every one of its requests starts with.</summary>
    Private Const AppOrigin As String = AppScheme & "://app/"

    ''' <summary>
    ''' WebView2's cache and storage. Explicitly under LocalAppData — the default sits beside the
    ''' executable, which fails outright when the bot is installed somewhere the user cannot write
    ''' to — and in a folder of its own, not wk's: two apps sharing one browser profile with
    ''' different scheme registrations refuse to start.
    ''' </summary>
    Private Shared ReadOnly Property UserDataFolder As String
        Get
            Return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wklol", "WebView2")
        End Get
    End Property

    ''' <summary>
    ''' Fills <paramref name="host"/> with the interface and connects it to <paramref name="bridge"/>.
    ''' Never throws: a failure paints the fallback panel and returns False.
    ''' </summary>
    Public Shared Async Function AttachAsync(host As Control, bridge As WebBridge) As Task(Of Boolean)
        If host Is Nothing Then Throw New ArgumentNullException(NameOf(host))
        If bridge Is Nothing Then Throw New ArgumentNullException(NameOf(bridge))

        If Not WebAssets.Present Then
            ShowFallback(host,
                "The interface is missing from this build.",
                "wklol.exe was built without the web folder inside it. Rebuild the project — " &
                "wklol.vbproj embeds everything under web\.")
            Return False
        End If

        Dim view As WebView2 = Nothing
        Try
            ' Asked for before anything is constructed: this is the call that tells a missing
            ' runtime apart from a real fault, and its exception is the one worth explaining.
            CoreWebView2Environment.GetAvailableBrowserVersionString()

            ' Until the page paints, what shows is the browser's own background — white by default.
            ' The host's colour is the page's own surface, so the gap reads as the window, not a flash.
            view = New WebView2() With {.Dock = DockStyle.Fill, .DefaultBackgroundColor = host.BackColor}
            host.Controls.Add(view)

            ' Secure, so the page is a secure context — storage depends on it. With an authority,
            ' so it has an origin at all: wk://app rather than one opaque origin per document.
            Dim scheme As New CoreWebView2CustomSchemeRegistration(AppScheme) With {
                .TreatAsSecure = True,
                .HasAuthorityComponent = True
            }
            Dim options As New CoreWebView2EnvironmentOptions(
                customSchemeRegistrations:=New List(Of CoreWebView2CustomSchemeRegistration) From {scheme})

            Dim env = Await CoreWebView2Environment.CreateAsync(Nothing, UserDataFolder, options)
            Await view.EnsureCoreWebView2Async(env)

            ' Light, whatever Windows is set to. The page never reads prefers-color-scheme — its
            ' theme is the user's choice — but the browser does, and on a dark Windows it filled the
            ' window black for a moment between the control appearing and the page's first frame.
            view.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light

            HardenForDesktop(view.CoreWebView2)
            KeepToOwnOrigin(view.CoreWebView2)

            view.CoreWebView2.AddWebResourceRequestedFilter(AppOrigin & "*", CoreWebView2WebResourceContext.All)
            AddHandler view.CoreWebView2.WebResourceRequested,
                Sub(sender, e) ServeAsset(env, e)

            ' Both directions of the bridge are wired before the first navigation, so a message
            ' the page sends on load cannot arrive before anything is listening.
            AddHandler view.CoreWebView2.WebMessageReceived,
                Sub(sender, e)
                    Try
                        ' KeepToOwnOrigin already stops the page from leaving; this is the same
                        ' rule at the door, for anything that got a document in regardless.
                        If Not IsOwnOrigin(e.Source) Then
                            Core.Logger.Warn("WebShell", "Ignored a message from outside the interface: " & e.Source)
                            Return
                        End If
                        bridge.HandleMessage(e.WebMessageAsJson)
                    Catch ex As Exception
                        Core.Logger.Warn("WebShell", "Bad message from the page: " & ex.Message)
                    End Try
                End Sub

            ' Posting has to happen on the UI thread. Handlers that finished on a task thread
            ' come back through here rather than each one remembering to marshal.
            '
            ' THE GUARD IS THIN ON PURPOSE. IsDisposed and InvokeRequired are the only two things
            ' a Control answers from ANY thread; CoreWebView2 is not one of them, so it is asked on
            ' the right side of the BeginInvoke — see PostSafely.
            '
            ' AND COMPLAINING ABOUT A FAILED POST IS ITSELF A LOG LINE, WHICH IS ANOTHER POST. So
            ' the first failure is reported and the rest are silent until one gets through.
            Dim postFailed As Boolean = False
            bridge.AttachPoster(
                Sub(json)
                    Try
                        If view.IsDisposed OrElse Not view.IsHandleCreated Then Return
                        If view.InvokeRequired Then view.BeginInvoke(Sub() PostSafely(view, json)) _
                                              Else PostSafely(view, json)
                        postFailed = False
                    Catch ex As Exception
                        If postFailed Then Return
                        postFailed = True
                        Core.Logger.Warn("WebShell", "Could not post to the page: " & ex.Message &
                                         " — further failures stay silent until one gets through.")
                    End Try
                End Sub)

            view.CoreWebView2.Navigate(AppOrigin & "index.html")
            Core.Logger.Info("WebShell", $"Interface loaded from wklol.exe ({WebAssets.Count} files)")
            Return True

        Catch ex As WebView2RuntimeNotFoundException
            If view IsNot Nothing Then host.Controls.Remove(view) : view.Dispose()
            ShowFallback(host,
                "The WebView2 runtime is not installed.",
                "The interface runs on Microsoft Edge WebView2, which ships with Windows 11 " &
                "and arrives on Windows 10 with Edge." & Environment.NewLine & Environment.NewLine &
                "Install the Evergreen Runtime from Microsoft, then reopen the bot.")
            Return False

        Catch ex As Exception
            If view IsNot Nothing Then host.Controls.Remove(view) : view.Dispose()
            Core.Logger.Warn("WebShell", "Could not start the interface: " & ex.Message)
            ShowFallback(host, "The interface could not start.", ex.Message)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Whether the last post was refused. Its complaint is rationed for the same reason the
    ''' poster's is — a warning about a failed post is a log line, and a log line is another post.
    ''' </summary>
    Private Shared _postComplained As Boolean

    ''' <summary>The post itself, always on the UI thread — the only place CoreWebView2 may be touched.</summary>
    Private Shared Sub PostSafely(view As WebView2, json As String)
        Try
            If Not view.IsDisposed AndAlso view.CoreWebView2 IsNot Nothing Then
                view.CoreWebView2.PostWebMessageAsJson(json)
                _postComplained = False
            End If
        Catch ex As Exception
            If _postComplained Then Return
            _postComplained = True
            Core.Logger.Warn("WebShell", "Post failed: " & ex.Message &
                             " — further failures stay silent until one gets through.")
        End Try
    End Sub

    ''' <summary>
    ''' Turns the browser into a window. Everything switched off here is something that makes
    ''' sense on a web page and nowhere else: a right-click menu offering Reload, Ctrl+P, pinch
    ''' zoom that leaves the layout at 140%, a status bar that pops up over the content.
    ''' </summary>
    Private Shared Sub HardenForDesktop(core As CoreWebView2)
        With core.Settings
            .AreDefaultContextMenusEnabled = False
            .AreBrowserAcceleratorKeysEnabled = False
            .IsStatusBarEnabled = False
            .IsZoomControlEnabled = False
            .IsSwipeNavigationEnabled = False
            .IsGeneralAutofillEnabled = False
            .IsPasswordAutosaveEnabled = False

            ' DevTools stay in Debug builds only — the console is where every mistake in app.js
            ' shows up, and F12 on a shipped bot is a support call waiting to happen.
#If DEBUG Then
            .AreDevToolsEnabled = True
#Else
            .AreDevToolsEnabled = False
#End If
        End With

        ' The interface is local and has no business opening windows.
        AddHandler core.NewWindowRequested,
            Sub(sender, e) e.Handled = True
    End Sub

    ''' <summary>
    ''' Answers one request under <see cref="AppOrigin"/> from the files built into the executable.
    ''' Raised on the UI thread and answered there: it is a dictionary lookup.
    ''' </summary>
    Private Shared Sub ServeAsset(env As CoreWebView2Environment, e As CoreWebView2WebResourceRequestedEventArgs)
        Try
            ' AbsolutePath leaves any query string behind — app.js?v=2 is still app.js.
            Dim path As String = Uri.UnescapeDataString(New Uri(e.Request.Uri).AbsolutePath).TrimStart("/"c)
            If path.Length = 0 Then path = "index.html"

            Dim bytes As Byte() = Nothing
            If WebAssets.TryGet(path, bytes) Then
                ' A stream per response over the one shared array: the browser reads it after this
                ' returns, on its own schedule, and must not share a read position with anyone.
                e.Response = env.CreateWebResourceResponse(
                    New MemoryStream(bytes, writable:=False), 200, "OK",
                    "Content-Type: " & WebAssets.ContentTypeOf(path))
            Else
                e.Response = env.CreateWebResourceResponse(Nothing, 404, "Not Found", "Content-Type: text/plain")
                Core.Logger.Warn("WebShell", "The page asked for a file the build does not have: " & path)
            End If
        Catch ex As Exception
            Core.Logger.Warn("WebShell", $"Could not answer {e.Request.Uri}: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' Pins the page to <see cref="AppOrigin"/>. A link, a script or a redirect that would take the
    ''' window anywhere else is cancelled, because whatever loaded there would hold the bridge.
    ''' </summary>
    Private Shared Sub KeepToOwnOrigin(browser As CoreWebView2)
        AddHandler browser.NavigationStarting,
            Sub(sender, e)
                If IsOwnOrigin(e.Uri) Then Return
                e.Cancel = True
                Core.Logger.Warn("WebShell", "Kept the interface from navigating to " & e.Uri)
            End Sub
    End Sub

    Private Shared Function IsOwnOrigin(uri As String) As Boolean
        Return uri IsNot Nothing AndAlso uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)
    End Function

    ''' <summary>
    ''' What the user sees instead of the interface when it cannot run. Deliberately plain
    ''' WinForms with stock controls — whatever is broken, this has to render.
    ''' </summary>
    Private Shared Sub ShowFallback(host As Control, title As String, detail As String)
        host.Controls.Clear()

        Dim panel As New TableLayoutPanel() With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = New Padding(28)
        }
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))

        panel.Controls.Add(New Label() With {
            .Text = title,
            .AutoSize = True,
            .Font = New Drawing.Font("Segoe UI", 12, Drawing.FontStyle.Bold),
            .Margin = New Padding(0, 0, 0, 10)
        }, 0, 0)

        panel.Controls.Add(New Label() With {
            .Text = detail,
            .AutoSize = True,
            .MaximumSize = New Drawing.Size(520, 0),
            .Font = New Drawing.Font("Segoe UI", 9.5F)
        }, 0, 1)

        host.Controls.Add(panel)
    End Sub

End Class
