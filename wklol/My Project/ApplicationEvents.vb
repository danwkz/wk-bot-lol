Namespace My

    ''' <summary>
    ''' Application-level events for the VB application model (MySubMain is on, so this class is what
    ''' actually starts the program — see Application.Designer.vb, which is generated and must not be
    ''' edited by hand). This half of the partial class is ours.
    ''' </summary>
    Partial Friend Class MyApplication

        ''' <summary>
        ''' The last word before the process ends.
        '''
        ''' Without a handler here the VB application model shows its own exception dialog and the
        ''' LOG says nothing — and a bot that disappears with no line on disk is a bug report nobody
        ''' can act on. So: write it down first, then say it, then end. The session log is named in
        ''' the message so the file is findable without knowing where it lives.
        ''' </summary>
        Private Sub MyApplication_UnhandledException(
                sender As Object,
                e As Microsoft.VisualBasic.ApplicationServices.UnhandledExceptionEventArgs) _
                Handles Me.UnhandledException

            Dim logPath As String = Nothing
            Try
                logPath = Global.wklol.Core.Logger.FilePath
                Global.wklol.Core.Logger.Error("App", "Unhandled exception — the bot is closing", e.Exception)
            Catch
                ' The logger failing is not a reason to skip telling the user.
            End Try

            Try
                Windows.Forms.MessageBox.Show(
                    "WK has to close." & vbCrLf & vbCrLf &
                    e.Exception.Message & vbCrLf & vbCrLf &
                    If(String.IsNullOrEmpty(logPath), "No session log was open.", "Session log: " & logPath),
                    "WK", Windows.Forms.MessageBoxButtons.OK, Windows.Forms.MessageBoxIcon.Error)
            Catch
            End Try

            e.ExitApplication = True
        End Sub

    End Class

End Namespace
