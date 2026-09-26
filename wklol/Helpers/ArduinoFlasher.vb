Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Windows.Forms
Imports wklol.Core

''' <summary>
''' Puts the wk_hid firmware onto a board.
'''
''' A board out of the box is not a half-ready keyboard, it is a different device: it enumerates as
''' a serial port and nothing else, answers no handshake, and cannot press a key. Nothing in
''' <see cref="ArduinoLink"/> can do anything about that — it can only report it — so this is the
''' one step that stands between a new board and the bot working at all.
'''
''' It shells out to arduino\flash.ps1 rather than reimplementing the upload, because that script
''' is also the documented way to do this by hand and two copies of an upload sequence would drift
''' apart. Everything here is about choosing the right arguments and turning the script's output
''' into something a person can act on.
''' </summary>
Public NotInheritable Class ArduinoFlasher

    Private Sub New()
    End Sub

    ''' <summary>
    ''' Where arduino\flash.ps1 is: beside the executable (the build copies it there), or up the
    ''' tree, which in a source checkout finds wklol\arduino.
    '''
    ''' Both exist when running from bin\Debug, and WHICH one is used matters for the
    ''' several-hundred-megabyte toolchain the script keeps beside itself. One that already has a
    ''' toolchain wins, and failing that the outermost, because bin\ is wiped by a Rebuild and a
    ''' toolchain downloaded there would be downloaded again after every clean build.
    ''' </summary>
    Public Shared Function FindScript() As String
        Dim candidates As New List(Of String)()
        Dim dir As String = Application.StartupPath
        For depth As Integer = 0 To 4
            If dir Is Nothing Then Exit For
            Dim candidate As String = Path.Combine(dir, "arduino", "flash.ps1")
            If File.Exists(candidate) AndAlso Not candidates.Contains(candidate) Then candidates.Add(candidate)
            Dim parent As DirectoryInfo = Directory.GetParent(dir)
            dir = If(parent Is Nothing, Nothing, parent.FullName)
        Next

        If candidates.Count = 0 Then Return Nothing
        For Each candidate As String In candidates
            If ToolchainPresent(candidate) Then Return candidate
        Next
        Return candidates(candidates.Count - 1)
    End Function

    ''' <summary>Whether the portable toolchain has already been downloaded beside the script.</summary>
    Public Shared Function ToolchainPresent(script As String) As Boolean
        If String.IsNullOrEmpty(script) Then Return False
        Try
            Return Directory.Exists(Path.Combine(Path.GetDirectoryName(script), ".toolchain", "data", "packages"))
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Which board definition to build for, from the USB ids Windows has for the port.
    '''
    ''' Worth doing rather than asking. "Pro Micro" is a board SHAPE sold by many people, and what
    ''' matters for the upload is whose BOOTLOADER is on it — most of the cheap ones carry
    ''' Arduino's (VID 2341), not SparkFun's (VID 1B4F), and picking the wrong one is a failed
    ''' upload with a message about avrdude that explains nothing.
    ''' </summary>
    Public Shared Function DetectFqbn(port As String) As String
        Const leonardo As String = "arduino:avr:leonardo"
        Try
            Dim query As String = "SELECT PNPDeviceID, Name FROM Win32_PnPEntity WHERE Name LIKE '%(" & port & ")%'"
            Using searcher As New Management.ManagementObjectSearcher(query)
                For Each item As Management.ManagementObject In searcher.Get()
                    Dim id As String = TryCast(item("PNPDeviceID"), String)
                    If id Is Nothing Then Continue For
                    id = id.ToUpperInvariant()
                    If id.Contains("VID_1B4F") Then Return "SparkFun:avr:promicro:cpu=16MHzatmega32U4"
                    If id.Contains("PID_8037") OrElse id.Contains("PID_0037") Then Return "arduino:avr:micro"
                    If id.Contains("VID_2341") OrElse id.Contains("VID_2A03") Then Return leonardo
                Next
            End Using
        Catch ex As Exception
            Logger.Info(ArduinoLink.LogSource, $"Could not read the board's USB ids ({ex.Message}) — assuming Leonardo")
        End Try
        Return leonardo
    End Function

    ''' <summary>
    ''' True when the port looks like a board this can flash at all — an ATmega32U4 with native
    ''' USB. Used to tell "your board is not flashed yet" apart from "that is a USB-to-serial
    ''' adapter and it can never be a keyboard".
    ''' </summary>
    Public Shared Function LooksLikeArduino(port As String) As Boolean
        Try
            Dim query As String = "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(" & port & ")%'"
            Using searcher As New Management.ManagementObjectSearcher(query)
                For Each item As Management.ManagementObject In searcher.Get()
                    Dim id As String = TryCast(item("PNPDeviceID"), String)
                    If id Is Nothing Then Continue For
                    id = id.ToUpperInvariant()
                    If id.Contains("VID_2341") OrElse id.Contains("VID_2A03") OrElse id.Contains("VID_1B4F") Then Return True
                Next
            End Using
        Catch
        End Try
        Return False
    End Function

    ''' <summary>
    ''' Runs the upload script, sending every line it prints to the log and to
    ''' <paramref name="progress"/>. Returns Nothing on success, or the reason it failed.
    '''
    ''' The output is streamed rather than collected because the first run spends minutes
    ''' downloading a toolchain, and a status that says "flashing…" for four minutes with nothing
    ''' behind it is indistinguishable from a hang.
    ''' </summary>
    Public Shared Function Run(script As String, port As String, fqbn As String,
                               Optional progress As Action(Of String) = Nothing) As String
        Dim psi As New Diagnostics.ProcessStartInfo() With {
            .FileName = "powershell.exe",
            .Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{script}"" " &
                         $"-Port {port} -Fqbn {fqbn}",
            .WorkingDirectory = Path.GetDirectoryName(script),
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }

        Dim say As Action(Of String) =
            Sub(line)
                Try
                    progress?.Invoke(line)
                Catch
                    ' Whoever is watching failing to watch is not a reason to stop the upload.
                End Try
            End Sub

        Dim errors As New List(Of String)()
        Using proc As New Diagnostics.Process()
            proc.StartInfo = psi
            AddHandler proc.OutputDataReceived,
                Sub(s, ev)
                    If String.IsNullOrWhiteSpace(ev.Data) Then Return
                    Dim line As String = ev.Data.Trim()
                    Logger.Info(ArduinoLink.LogSource, line)
                    say(line)
                End Sub
            AddHandler proc.ErrorDataReceived,
                Sub(s, ev)
                    If String.IsNullOrWhiteSpace(ev.Data) Then Return
                    Dim line As String = ev.Data.Trim()
                    SyncLock errors
                        errors.Add(line)
                    End SyncLock
                    Logger.Warn(ArduinoLink.LogSource, line)
                End Sub

            proc.Start()
            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()

            ' Generous: a cold first run downloads a 50 MB compiler and a 200 MB core. Bounded
            ' anyway, so a script that hangs cannot leave the caller waiting forever.
            If Not proc.WaitForExit(15 * 60 * 1000) Then
                Try : proc.Kill() : Catch : End Try
                Return "it took longer than 15 minutes and was stopped"
            End If

            If proc.ExitCode = 0 Then Return Nothing
            SyncLock errors
                Return Summarise(errors, proc.ExitCode)
            End SyncLock
        End Using
    End Function

    ''' <summary>
    ''' Turns the upload script's error output into one sentence worth showing.
    '''
    ''' Not simply the last line: PowerShell reports a failure as a paragraph wrapped at the console
    ''' width, so the LAST line of a failed flash is a fragment that says nothing about anything.
    '''
    ''' The port being busy gets its own answer because it is the failure that will actually
    ''' happen: another copy of the bot, a serial monitor or the Arduino IDE is holding the board,
    ''' and avrdude's "Acesso negado" does not tell anyone what to do about it.
    ''' </summary>
    Private Shared Function Summarise(errors As List(Of String), exitCode As Integer) As String
        For Each line As String In errors
            If line.IndexOf("cannot open port", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
               line.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
               line.IndexOf("Acesso negado", StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return "another program is holding the board's port. Close any other copy of the bot, " &
                       "serial monitor or Arduino IDE that has it open, then try again."
            End If
        Next

        ' The first real line, skipping PowerShell's own decoration around it.
        For Each line As String In errors
            If line.StartsWith("+", StringComparison.Ordinal) Then Continue For
            If line.StartsWith("~", StringComparison.Ordinal) Then Continue For
            If line.StartsWith("CategoryInfo", StringComparison.Ordinal) Then Continue For
            If line.StartsWith("FullyQualifiedErrorId", StringComparison.Ordinal) Then Continue For
            If line.StartsWith("No ", StringComparison.Ordinal) OrElse
               line.StartsWith("At ", StringComparison.Ordinal) Then Continue For   ' "At <file>:<line>"
            Return line
        Next

        Return $"the upload script exited with code {exitCode}"
    End Function

End Class
