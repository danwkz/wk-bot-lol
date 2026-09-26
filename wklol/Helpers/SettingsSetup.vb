Imports Newtonsoft.Json.Linq
Imports wklol.Core

' ══════════════════════════════════════════════════════════════════════════════
' SettingsSetup  —  the Settings screen's view of the input, and its edits.
'
' HOW the bot types and clicks rather than WHAT it does: which port the board is
' on, and how long a key or a button stays down. None of it belongs to a module,
' which is exactly why it is its own screen. The port is REPORTED here and picked
' in Setup, where the board is found — one home per setting.
' ══════════════════════════════════════════════════════════════════════════════

Public NotInheritable Class SettingsSetup

    Private Sub New()
    End Sub

    ''' <summary>The longest a key or a button may be held, in milliseconds.</summary>
    Public Const MaxHoldMs As Integer = 1000

    Public Shared Function Describe(store As SettingsStore) As Object
        Dim inp = store.Settings.Input
        Return New With {
            .board = New With {
                .port = If(String.IsNullOrWhiteSpace(inp.ArduinoPort), Nothing, inp.ArduinoPort),
                .ready = inp.ArduinoReady},
            .input = New With {
                .keyMin = inp.KeyHoldMin,
                .keyMax = inp.KeyHoldMax,
                .clickMin = inp.ClickHoldMin,
                .clickMax = inp.ClickHoldMax,
                .max = MaxHoldMs}
        }
    End Function

    ''' <summary>
    ''' The timings. Every field is sent every time, and a minimum above its maximum is put in
    ''' order rather than refused — it is a typo, not a decision.
    ''' </summary>
    Public Shared Sub SaveInput(store As SettingsStore, data As JObject)
        Dim inp = store.Settings.Input

        Dim keyA = SetupFields.Clamp(SetupFields.IntOf(data, "keyMin", inp.KeyHoldMin), 1, MaxHoldMs)
        Dim keyB = SetupFields.Clamp(SetupFields.IntOf(data, "keyMax", inp.KeyHoldMax), 1, MaxHoldMs)
        inp.KeyHoldMin = Math.Min(keyA, keyB)
        inp.KeyHoldMax = Math.Max(keyA, keyB)

        Dim clickA = SetupFields.Clamp(SetupFields.IntOf(data, "clickMin", inp.ClickHoldMin), 1, MaxHoldMs)
        Dim clickB = SetupFields.Clamp(SetupFields.IntOf(data, "clickMax", inp.ClickHoldMax), 1, MaxHoldMs)
        inp.ClickHoldMin = Math.Min(clickA, clickB)
        inp.ClickHoldMax = Math.Max(clickA, clickB)

        store.Save()
        Logger.Info("Settings", $"Key held {inp.KeyHoldMin}–{inp.KeyHoldMax} ms, " &
                                $"click held {inp.ClickHoldMin}–{inp.ClickHoldMax} ms")
    End Sub

End Class
