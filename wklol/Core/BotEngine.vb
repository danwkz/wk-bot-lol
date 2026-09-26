Imports System.Collections.Concurrent
Imports System.Threading

Namespace Core

    ''' Top-level orchestrator. Owns the module hosts and the context they are handed, and
    ''' provides per-module control with deterministic stop.
    '''
    ''' What wk's engine also carries and this one does not: the action governor, the global
    ''' pause gate and the foreground watcher. Each exists there to protect a game client window
    ''' this bot does not have, and the Orb Walker paces itself — see BotContext.
    Public NotInheritable Class BotEngine

        Private Shared ReadOnly _instance As New Lazy(Of BotEngine)(
            Function() New BotEngine(),
            LazyThreadSafetyMode.ExecutionAndPublication)

        Public Shared ReadOnly Property Instance As BotEngine
            Get
                Return _instance.Value
            End Get
        End Property

        Private ReadOnly _hosts As New ConcurrentDictionary(Of String, ModuleHost)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _context As New BotContext()

        Private Sub New()
        End Sub

        ' --- Registration -------------------------------------------------------------

        Public Sub Register(botModule As IBotModule)
            If botModule Is Nothing Then Throw New ArgumentNullException(NameOf(botModule))
            Dim host As New ModuleHost(botModule, _context)
            If Not _hosts.TryAdd(botModule.Name, host) Then
                Throw New InvalidOperationException("Module already registered: " & botModule.Name)
            End If
        End Sub

        Public Function GetState(moduleName As String) As ModuleState
            Dim host As ModuleHost = Nothing
            If _hosts.TryGetValue(moduleName, host) Then Return host.State
            Return ModuleState.Stopped
        End Function

        ' --- Control ------------------------------------------------------------------

        Public Sub StartModule(name As String)
            Dim host As ModuleHost = Nothing
            If _hosts.TryGetValue(name, host) Then host.Start()
        End Sub

        Public Sub StopModule(name As String, Optional joinTimeoutMs As Integer = 2000)
            Dim host As ModuleHost = Nothing
            If _hosts.TryGetValue(name, host) Then host.Stop(joinTimeoutMs)
        End Sub

        ''' <summary>Stops every registered module. Used on the way out.</summary>
        Public Sub StopAll(Optional joinTimeoutMs As Integer = 2000)
            For Each host In _hosts.Values
                Try
                    host.Stop(joinTimeoutMs)
                Catch ex As Exception
                    Logger.Error("Engine", "Stopping module failed: " & host.Name, ex)
                End Try
            Next
            Logger.Info("Engine", "Stopped.")
        End Sub

    End Class

End Namespace
