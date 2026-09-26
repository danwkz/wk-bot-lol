Imports System.Threading

Namespace Core

    ''' Contract every bot behavior implements. The host thread owns the
    ''' lifecycle and calls these in order: Initialize -> Tick (looping) -> Shutdown.
    ''' Implementations must keep individual ticks short and respect the token.
    Public Interface IBotModule

        ReadOnly Property Name As String

        Sub Initialize(context As BotContext)

        Sub Tick(ct As CancellationToken)

        Sub Shutdown()

    End Interface

End Namespace
