Imports System.Collections.Generic
Imports System.IO
Imports System.Text

Namespace Core

    Public Enum LogLevel
        Info
        Warn
        [Error]
        ''' <summary>
        ''' Diagnostic detail: kept in the ring and the session file, never drawn on the console
        ''' unless it is asked for. The split is between what is WRITTEN and what is SHOWN, not
        ''' between what is worth keeping and what is not.
        '''
        ''' Last in the enum so the values of the three levels above it do not move.
        ''' </summary>
        Trace
    End Enum

    Public Class LogEventArgs
        Inherits EventArgs

        ''' <summary>
        ''' Where this line sits in the session, counted from 1.
        '''
        ''' Assigned under the writer's own lock, which is what makes it an ORDER: two threads
        ''' logging in the same millisecond get the same timestamp and different sequences, and
        ''' the sequence is the one that says which happened first.
        '''
        ''' The console reads it as an identity as well — it is how a screen knows which lines it
        ''' has already been shown, so a batch that arrives twice is not printed twice.
        ''' </summary>
        Public ReadOnly Property Sequence As Long
        Public ReadOnly Property Timestamp As DateTime
        Public ReadOnly Property Level As LogLevel
        Public ReadOnly Property Source As String
        Public ReadOnly Property Message As String

        Public Sub New(timestamp As DateTime, level As LogLevel, source As String, message As String,
                       Optional sequence As Long = 0)
            Me.Timestamp = timestamp
            Me.Level = level
            Me.Source = source
            Me.Message = message
            Me.Sequence = sequence
        End Sub

        ''' <summary>
        ''' The line as it goes to the file and to the clipboard. Local time to the millisecond:
        ''' the console shows whole seconds, which is fine for reading along but useless for the
        ''' one thing the log is asked to settle — what happened between two key presses.
        ''' </summary>
        Public Function Format() As String
            Return $"[{Timestamp.ToLocalTime():HH:mm:ss.fff}][{LevelTag(Level)}][{Source}] {Message}"
        End Function

        Private Shared Function LevelTag(level As LogLevel) As String
            Select Case level
                Case LogLevel.Warn : Return "WARN"
                Case LogLevel.Error : Return "ERR "
                Case LogLevel.Trace : Return "TRC "
                Case Else : Return "INFO"
            End Select
        End Function

    End Class

    ''' <summary>
    ''' Thread-safe log sink. The UI subscribes to <see cref="LogWritten"/> to render, but the
    ''' event is not the record: by the time something odd is noticed the evidence for it has
    ''' usually scrolled away. So every line also lands in two places that outlive the console — a
    ''' fixed-size ring in memory (<see cref="Snapshot"/>) and a file for the session.
    ''' </summary>
    Public NotInheritable Class Logger

        Private Shared ReadOnly _writeGate As New Object()

        ''' <summary>Lines kept in memory — several minutes of the busiest case.</summary>
        Private Const RingCapacity As Integer = 4000

        Private Shared ReadOnly _ring As New Queue(Of LogEventArgs)(RingCapacity)
        Private Shared _file As StreamWriter
        Private Shared _filePath As String
        Private Shared _fileFailed As Boolean

        ''' <summary>
        ''' Lines written this session. Only ever touched under <c>_writeGate</c>; it is what
        ''' <see cref="LogEventArgs.Sequence"/> is cut from.
        ''' </summary>
        Private Shared _written As Long

        Public Shared Event LogWritten As EventHandler(Of LogEventArgs)

        ''' <summary>Full path of this session's log file, or Nothing when none could be opened.</summary>
        Public Shared ReadOnly Property FilePath As String
            Get
                SyncLock _writeGate
                    Return _filePath
                End SyncLock
            End Get
        End Property

        Public Shared Sub Info(source As String, message As String)
            Write(LogLevel.Info, source, message)
        End Sub

        Public Shared Sub Warn(source As String, message As String)
            Write(LogLevel.Warn, source, message)
        End Sub

        ''' <summary>
        ''' Per-tick diagnostic detail. Goes to the ring and the session file; the console does not
        ''' draw it. See <see cref="LogLevel.Trace"/>.
        ''' </summary>
        Public Shared Sub Trace(source As String, message As String)
            Write(LogLevel.Trace, source, message)
        End Sub

        Public Shared Sub [Error](source As String, message As String, Optional ex As Exception = Nothing)
            Dim payload = If(ex Is Nothing, message, message & " :: " & ex.ToString())
            Write(LogLevel.Error, source, payload)
        End Sub

        ''' <summary>
        ''' Everything the ring is holding, oldest first — what a console fills itself from when
        ''' it opens, so it starts with the session so far rather than with the next line written.
        '''
        ''' A copy, taken under the lock. Handing the queue itself out would be a collection
        ''' another thread is enqueueing into while the caller walks it.
        ''' </summary>
        Public Shared Function Snapshot(includeTrace As Boolean) As LogEventArgs()
            SyncLock _writeGate
                If includeTrace Then Return _ring.ToArray()
                Return _ring.Where(Function(e) e.Level <> LogLevel.Trace).ToArray()
            End SyncLock
        End Function

        ''' <summary>The sequence of the last line written, or 0 when nothing has been.</summary>
        Public Shared ReadOnly Property LastSequence As Long
            Get
                SyncLock _writeGate
                    Return _written
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Opens the session log file. Called once at startup; a failure here is not worth
        ''' stopping for, so it is reported to the ring and the bot runs without a file.
        ''' </summary>
        Public Shared Sub OpenFile(directory As String)
            SyncLock _writeGate
                If _file IsNot Nothing OrElse _fileFailed Then Return
                Try
                    IO.Directory.CreateDirectory(directory)
                    Dim target As String = Path.Combine(directory, $"wklol-{DateTime.Now:yyyyMMdd-HHmmss}.log")
                    ' AutoFlush: the lines worth having are the ones written just before whatever
                    ' went wrong, and a buffer holds exactly those back.
                    _file = New StreamWriter(target, append:=True, encoding:=New UTF8Encoding(False)) With {
                        .AutoFlush = True
                    }
                    _filePath = target
                Catch ex As Exception
                    _fileFailed = True
                    _file = Nothing
                    _filePath = Nothing
                    Debug.WriteLine($"Logger: could not open the log file: {ex.Message}")
                End Try
            End SyncLock
        End Sub

        Public Shared Sub CloseFile()
            SyncLock _writeGate
                Try
                    _file?.Dispose()
                Catch
                End Try
                _file = Nothing
            End SyncLock
        End Sub

        Private Shared Sub Write(level As LogLevel, source As String, message As String)
            ' Built INSIDE the lock, because the sequence is only an order if the number and the
            ' position in the ring are handed out together.
            Dim e As LogEventArgs
            Dim line As String
            SyncLock _writeGate
                _written += 1
                e = New LogEventArgs(DateTime.UtcNow, level, source, message, _written)
                line = e.Format()
                Diagnostics.Debug.WriteLine(line)
                _ring.Enqueue(e)
                While _ring.Count > RingCapacity
                    _ring.Dequeue()
                End While
                If _file IsNot Nothing Then
                    Try
                        _file.WriteLine(line)
                    Catch
                        ' A disk that has stopped accepting writes must not take the bot down with
                        ' it, and must not be retried once per line for the rest of the session.
                        Try
                            _file.Dispose()
                        Catch
                        End Try
                        _file = Nothing
                        _fileFailed = True
                    End Try
                End If
            End SyncLock
            RaiseEvent LogWritten(Nothing, e)
        End Sub

    End Class

End Namespace
