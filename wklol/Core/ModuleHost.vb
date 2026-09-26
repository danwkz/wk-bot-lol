Imports System.Threading

Namespace Core

    ''' Owns one dedicated thread for a single module plus its cancellation source and
    ''' lifecycle state. Designed for deterministic stop.
    Friend NotInheritable Class ModuleHost

        Private ReadOnly _module As IBotModule
        Private ReadOnly _context As BotContext
        Private _cts As CancellationTokenSource
        Private _thread As Thread
        Private _state As Integer = CInt(ModuleState.Stopped)

        Public Sub New(botModule As IBotModule, context As BotContext)
            _module = botModule
            _context = context
        End Sub

        Public ReadOnly Property Name As String
            Get
                Return _module.Name
            End Get
        End Property

        Public ReadOnly Property State As ModuleState
            Get
                Return CType(Volatile.Read(_state), ModuleState)
            End Get
        End Property

        Public Sub Start()
            ' Only allow Start from a fully stopped state.
            If Interlocked.CompareExchange(_state, CInt(ModuleState.Starting), CInt(ModuleState.Stopped)) <> CInt(ModuleState.Stopped) Then Return
            _cts = New CancellationTokenSource()
            _thread = New Thread(AddressOf RunLoop) With {
                .IsBackground = True,
                .Name = "BotModule:" & _module.Name,
                .Priority = ThreadPriority.AboveNormal
            }
            _thread.Start()
        End Sub

        Public Sub [Stop](Optional joinTimeoutMs As Integer = 2000)
            Dim current = State
            If current = ModuleState.Stopped OrElse current = ModuleState.Stopping Then Return
            Volatile.Write(_state, CInt(ModuleState.Stopping))
            Try
                _cts?.Cancel()
            Catch
            End Try
            If _thread IsNot Nothing AndAlso _thread.IsAlive Then
                _thread.Join(joinTimeoutMs)
            End If
        End Sub

        Private Sub RunLoop()
            Dim ct = _cts.Token
            Try
                _module.Initialize(_context)
                Volatile.Write(_state, CInt(ModuleState.Running))

                While Not ct.IsCancellationRequested
                    Try
                        _module.Tick(ct)
                    Catch ocex As OperationCanceledException
                        Exit While
                    Catch ex As Exception
                        Logger.Error(_module.Name, "Tick threw", ex)
                        ' Backoff before retrying so a faulty module does not spin a core.
                        ct.WaitHandle.WaitOne(50)
                    End Try
                End While
            Catch ex As Exception
                Logger.Error(_module.Name, "Initialize threw", ex)
                Volatile.Write(_state, CInt(ModuleState.Faulted))
                Return
            Finally
                Try
                    _module.Shutdown()
                Catch ex As Exception
                    Logger.Error(_module.Name, "Shutdown threw", ex)
                End Try
                Try
                    _cts?.Dispose()
                Catch
                End Try
                _cts = Nothing
                Volatile.Write(_state, CInt(ModuleState.Stopped))
            End Try
        End Sub

    End Class

End Namespace
