Imports System.IO

' ══════════════════════════════════════════════════════════════════════════════
' WebAssets  —  the HTML interface as it ships: inside wklol.exe.
'
' WHY EMBEDDED. A folder of loose files beside the executable would be an
' editable interface: change a line of app.js, relaunch, and the bot runs the
' change. Built into the assembly there is no file to edit, and nothing in the
' install folder shows the front end at all.
'
' WHAT THAT DOES NOT BUY. A higher wall, not a lock. Anything shipped to the
' user's machine can be taken out of it by someone determined enough to take
' wklol.exe apart, and a running page can be driven by anything that reaches the
' browser — the door WebShell closes separately. The protection that holds
' whatever happens to this page is that the page decides nothing: every command
' is checked again in WebBridge, against the objects the engine reads, so a
' page that lies about what is allowed is refused.
'
' The build names each file "web/" plus its path under web\ (see wklol.vbproj),
' which is the only thing this class looks for.
' ══════════════════════════════════════════════════════════════════════════════

Public NotInheritable Class WebAssets

    Private Sub New()
    End Sub

    ''' <summary>What the build puts in front of every interface file's resource name.</summary>
    Private Const Prefix As String = "web/"

    Private Shared _files As Dictionary(Of String, Byte())
    Private Shared ReadOnly _gate As New Object()

    ''' <summary>
    ''' Every interface file by its path under web\, forward slashes, any case ("app.js").
    ''' Read out of the assembly once, on first use, and kept: half a megabyte, and it turns every
    ''' request the page makes into a dictionary lookup.
    ''' </summary>
    Private Shared ReadOnly Property Files As Dictionary(Of String, Byte())
        Get
            SyncLock _gate
                If _files Is Nothing Then _files = Load()
                Return _files
            End SyncLock
        End Get
    End Property

    Private Shared Function Load() As Dictionary(Of String, Byte())
        Dim found As New Dictionary(Of String, Byte())(StringComparer.OrdinalIgnoreCase)
        Dim assembly = GetType(WebAssets).Assembly
        For Each name In assembly.GetManifestResourceNames()
            If Not name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) Then Continue For
            Using stream = assembly.GetManifestResourceStream(name), copy As New MemoryStream()
                stream.CopyTo(copy)
                ' %(RecursiveDir) spells a subfolder with a backslash; the page asks with slashes.
                found(name.Substring(Prefix.Length).Replace("\"c, "/"c)) = copy.ToArray()
            End Using
        Next
        Return found
    End Function

    ''' <summary>
    ''' Whether the build put the interface in at all. False is a broken build — a project file
    ''' that lost its web\ item — never something an install can cause or fix.
    ''' </summary>
    Public Shared ReadOnly Property Present As Boolean
        Get
            Return Files.ContainsKey("index.html")
        End Get
    End Property

    ''' <summary>How many files the build put in. For the log line that says where the page came from.</summary>
    Public Shared ReadOnly Property Count As Integer
        Get
            Return Files.Count
        End Get
    End Property

    ''' <summary>One file's bytes. <paramref name="path"/> is relative to web\, with forward slashes.</summary>
    Public Shared Function TryGet(path As String, ByRef bytes As Byte()) As Boolean
        If String.IsNullOrEmpty(path) Then Return False
        Return Files.TryGetValue(path, bytes)
    End Function

    ''' <summary>
    ''' The Content-Type the page's request is answered with. It matters more here than from a
    ''' server: a script or stylesheet served without its type is refused outright, and the page
    ''' comes up unstyled or dead with nothing but a console line to say why.
    ''' </summary>
    Public Shared Function ContentTypeOf(path As String) As String
        Select Case IO.Path.GetExtension(path).ToLowerInvariant()
            Case ".html", ".htm" : Return "text/html; charset=utf-8"
            Case ".css" : Return "text/css; charset=utf-8"
            Case ".js", ".mjs" : Return "text/javascript; charset=utf-8"
            Case ".json" : Return "application/json; charset=utf-8"
            Case ".svg" : Return "image/svg+xml"
            Case ".png" : Return "image/png"
            Case ".jpg", ".jpeg" : Return "image/jpeg"
            Case ".gif" : Return "image/gif"
            Case ".webp" : Return "image/webp"
            Case ".ico" : Return "image/x-icon"
            Case ".woff2" : Return "font/woff2"
            Case ".woff" : Return "font/woff"
            Case ".wav" : Return "audio/wav"
            Case ".mp3" : Return "audio/mpeg"
            Case Else : Return "application/octet-stream"
        End Select
    End Function

End Class
