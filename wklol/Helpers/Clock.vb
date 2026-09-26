Imports System.Diagnostics

''' <summary>
''' The one clock anything in this bot may time itself against.
'''
''' WHY NOT <c>Environment.TickCount</c>. It is a 32-bit count of milliseconds since the machine
''' booted, so it OVERFLOWS after 24,9 days and carries on from Int32.MinValue. A notebook that is
''' closed rather than shut down passes that mark without anybody noticing, and from then on every
''' "has enough time passed" written the obvious way answers NO, for weeks — wk lost two sessions to
''' exactly that.
'''
''' A Stopwatch has none of it: 64-bit, monotone, starts at zero when the process does, and every
''' difference is a real elapsed time.
''' </summary>
Public NotInheritable Class Clock

    Private Sub New()
    End Sub

    Private Shared ReadOnly _since As Stopwatch = Stopwatch.StartNew()

    ''' <summary>Milliseconds since the bot started. Never negative, never wraps, never goes
    ''' backwards — unlike the wall clock, which a user or a time server can move under us.</summary>
    Public Shared ReadOnly Property NowMs As Long
        Get
            Return _since.ElapsedMilliseconds
        End Get
    End Property

End Class
