Imports System.Globalization
Imports System.Threading
Imports wklol.Core

' ══════════════════════════════════════════════════════════════════════════════
' LogFeed  —  the Log screen's end of the logger.
'
' The interface's console is a READER, not a second record. Every line is already
' in two places that outlive it — Logger's ring and this session's file — so
' nothing here is the only copy of anything. What it does is get those lines onto
' a web page fast enough to read along with, without letting a busy moment cost
' the window anything.
'
' THREE THINGS EARN THEIR COMPLEXITY.
'
'   BATCHING. Logger raises its event from whatever thread wrote the line, and a
'   busy moment with the trace on writes ten a second. One postMessage per line is ten
'   marshals to the UI thread, ten JSON documents and ten DOM appends a second,
'   for output a human reads in blocks anyway. Lines are queued as they arrive
'   and flushed together a tenth of a second later, so a burst costs one message
'   instead of fifty. The timer is only ever armed when something is waiting — an
'   idle bot costs nothing at all.
'
'   TRACE IS OPT-IN, AT THE SOURCE. The console does not draw trace lines unless
'   it is asked to, and while it is not asking they are dropped HERE rather than
'   filtered in the page. A dropped line is one that was never serialised, never
'   posted and never walked — the whole difference between a console that is free
'   to leave open and one that is not. The ring keeps them throughout, so turning
'   trace on fills the screen from it rather than starting blank.
'
'   CLEAR IS A FLOOR, NOT A DELETION. Clearing the console moves a mark up to the
'   last line written, and snapshots after it start above the mark. The ring and
'   the file are untouched, because "I do not want to look at this any more" and
'   "this never happened" are different requests and only the first was made.
' ══════════════════════════════════════════════════════════════════════════════

Public NotInheritable Class LogFeed
    Implements IDisposable

    ''' <summary>
    ''' How long lines wait to travel together. Short enough that the console reads as live —
    ''' well under the ~200 ms at which a delay starts to feel like lag — and long enough that a
    ''' burst of fifty lines is one message rather than fifty.
    ''' </summary>
    Private Const FlushMs As Integer = 120

    ''' <summary>
    ''' The most lines that may be waiting for the next flush.
    '''
    ''' A page that has stopped answering — a browser that never started, a window mid-teardown —
    ''' must not turn the logger into a memory leak. Past this the OLDEST waiting lines are
    ''' dropped and counted, and the count travels with the next batch so the console can say it
    ''' missed some rather than quietly showing a gap. The ring still holds every one of them;
    ''' only this screen's live view skipped them.
    ''' </summary>
    Private Const MaxPending As Integer = 2000

    Private ReadOnly _gate As New Object()
    Private ReadOnly _push As Action(Of String, Object)
    Private ReadOnly _pending As New Queue(Of LogEventArgs)()
    Private ReadOnly _handler As EventHandler(Of LogEventArgs)
    Private ReadOnly _timer As Timer

    Private _scheduled As Boolean
    Private _dropped As Long
    Private _trace As Boolean
    Private _floor As Long

    ''' <summary>The session file as the page was last told it. See Flush.</summary>
    Private _file As String

    ''' <summary>
    ''' Set while this thread is inside <see cref="Flush"/>'s push, so a line written BY the push
    ''' — the browser host complaining that it could not post — is not itself queued for the next
    ''' push. Without it the two feed each other: a failed post logs, the log makes a batch, the
    ''' batch fails to post, and it repeats every hundred milliseconds for the rest of the session.
    ''' It cost one, and this is the belt to the poster's braces.
    '''
    ''' The line is not lost — the ring and the session file take it like any other, and the next
    ''' snapshot the console asks for has it.
    ''' </summary>
    <ThreadStatic>
    Private Shared _pushing As Boolean

    ''' <param name="push">The bridge's own Push — event name, payload.</param>
    Public Sub New(push As Action(Of String, Object))
        If push Is Nothing Then Throw New ArgumentNullException(NameOf(push))
        _push = push

        ' Created stopped. Change() arms it for a single shot each time a line arrives, and the
        ' callback disarms it by simply not asking for another.
        _timer = New Timer(AddressOf Flush, Nothing, Timeout.Infinite, Timeout.Infinite)

        ' Held in a field so Dispose can take exactly this handler off again. Logger is static and
        ' lives as long as the process, so a handler left on it would keep this feed — and the
        ' bridge behind it — alive for the rest of the session.
        _handler = AddressOf OnLogWritten
        AddHandler Logger.LogWritten, _handler
    End Sub

    ''' <summary>
    ''' Whether trace lines are forwarded to the page. Off by default — see the header. Switching
    ''' it off drops the trace lines already waiting too, so the last batch before the switch does
    ''' not land after it.
    ''' </summary>
    Public Property Trace As Boolean
        Get
            SyncLock _gate
                Return _trace
            End SyncLock
        End Get
        Set(value As Boolean)
            SyncLock _gate
                _trace = value
                If Not value Then Keep(Function(e) e.Level <> LogLevel.Trace)
            End SyncLock
        End Set
    End Property

    ''' <summary>Empties the console without touching the record. Lines written from now on arrive.</summary>
    Public Sub Clear()
        SyncLock _gate
            _floor = Logger.LastSequence
            _dropped = 0
            Keep(Function(e) e.Sequence > _floor)
        End SyncLock
    End Sub

    ''' <summary>
    ''' What the console fills itself from when it opens: the ring, minus whatever was cleared,
    ''' minus the trace unless it has been asked for.
    ''' </summary>
    Public Function Describe() As Object
        Dim trace As Boolean
        Dim floor As Long
        Dim dropped As Long
        SyncLock _gate
            trace = _trace
            floor = _floor
            dropped = _dropped
        End SyncLock

        Dim rows = Logger.Snapshot(trace).
            Where(Function(e) e.Sequence > floor).
            Select(AddressOf Project).
            ToArray()

        Return New With {
            .rows = rows,
            .trace = trace,
            .dropped = dropped,
            .file = Logger.FilePath
        }
    End Function

    ' ── Inbound ──────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Called from whatever thread wrote the line. Does as little as a log call can be asked to
    ''' pay for: one lock, one enqueue, and arming a timer that is usually already armed.
    ''' </summary>
    Private Sub OnLogWritten(sender As Object, e As LogEventArgs)
        If e Is Nothing OrElse _pushing Then Return

        SyncLock _gate
            If e.Level = LogLevel.Trace AndAlso Not _trace Then Return
            If e.Sequence <= _floor Then Return

            _pending.Enqueue(e)
            While _pending.Count > MaxPending
                _pending.Dequeue()
                _dropped += 1
            End While

            If _scheduled Then Return
            _scheduled = True
        End SyncLock

        Try
            _timer.Change(FlushMs, Timeout.Infinite)
        Catch ex As ObjectDisposedException
            ' Disposed between the enqueue and here: the session is ending, and the lines waiting
            ' on this flush are in the ring and the file like every other one.
        End Try
    End Sub

    ''' <summary>
    ''' Sends everything waiting as one event. Runs on a pool thread; getting it to the page is
    ''' the bridge's problem and its poster marshals for us — see WebShell.
    ''' </summary>
    Private Sub Flush(state As Object)
        Dim rows As Object()
        Dim dropped As Long

        SyncLock _gate
            _scheduled = False
            If _pending.Count = 0 AndAlso _dropped = 0 Then Return
            rows = _pending.Select(AddressOf Project).ToArray()
            _pending.Clear()
            dropped = _dropped
            _dropped = 0
        End SyncLock

        ' The session file is opened a moment AFTER the bridge is built, so a console that asked
        ' for its state early was told there was no file and had no reason to ask again. Sent
        ' with the first batch after it changes, and Nothing on every batch after that.
        Dim file As String = Nothing
        Dim current As String = Logger.FilePath
        SyncLock _gate
            If Not String.Equals(current, _file, StringComparison.Ordinal) Then
                _file = current
                file = current
            End If
        End SyncLock

        Try
            _pushing = True
            _push("log", New With {.rows = rows, .dropped = dropped, .file = file})
        Catch ex As Exception
            ' DELIBERATELY NOT LOGGED. A warning written from here would be a line, which arrives
            ' back through OnLogWritten, which arms the timer, which fails to post and warns
            ' again — a loop that outlives whatever caused it. The poster reports its own
            ' failures, and this screen is a reader: a batch of it going missing costs nothing.
            Diagnostics.Debug.WriteLine("LogFeed: could not push a batch: " & ex.Message)
        Finally
            _pushing = False
        End Try
    End Sub

    ' ── Plumbing ─────────────────────────────────────────────────────────────

    ''' <summary>
    ''' One line, as the page reads it. <c>level</c> is <see cref="LogLevel"/>'s own numbering —
    ''' 0 info, 1 warn, 2 error, 3 trace — so the two sides cannot drift apart over a spelling.
    '''
    ''' The time is local, to the millisecond, and formatted HERE: a key press lasts tens of ms, which is
    ''' the resolution the log exists to settle, and invariant formatting keeps the colon a colon
    ''' on a machine whose culture would have written something else.
    ''' </summary>
    Private Shared Function Project(e As LogEventArgs) As Object
        Return New With {
            .seq = e.Sequence,
            .time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            .level = CInt(e.Level),
            .source = e.Source,
            .text = e.Message
        }
    End Function

    ''' <summary>Rebuilds the pending queue with only what <paramref name="keep"/> accepts.</summary>
    Private Sub Keep(keep As Func(Of LogEventArgs, Boolean))
        Dim kept = _pending.Where(keep).ToArray()
        _pending.Clear()
        For Each e In kept
            _pending.Enqueue(e)
        Next
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Try
            RemoveHandler Logger.LogWritten, _handler
        Catch
        End Try
        Try
            _timer.Dispose()
        Catch
        End Try
    End Sub

End Class
