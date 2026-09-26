Imports Newtonsoft.Json.Linq
Imports System.Windows.Forms
Imports wklol.Core

' ══════════════════════════════════════════════════════════════════════════════
' OrbWalkerSetup  —  the Orb Walker screen's view of its settings, and its edits.
'
' ONE LIST OF PROBLEMS. What stops the module from being switched on is worked
' out in Problems and nowhere else: the screen prints it, Home counts it, and
' the switch is refused with its first line. A rule checked in two places is a
' rule that will one day be checked differently in one of them.
'
' A KEY IS REFUSED WHEN IT IS CAPTURED, not when the module starts: binding the
' master key to the key the cycle presses would have the bot swallow its own
' keystrokes, and the moment to say so is while the user is looking at it.
' ══════════════════════════════════════════════════════════════════════════════

Public NotInheritable Class OrbWalkerSetup

    Private Sub New()
    End Sub

    Public Const MinDelayMs As Integer = 1
    Public Const MaxDelayMs As Integer = 5000

    ''' <summary>The three keys, by the names the page uses for them.</summary>
    Public Const SlotMaster As String = "master"
    Public Const SlotHold As String = "hold"
    Public Const SlotPress As String = "press"

    Private Shared ReadOnly ButtonNames As String() = {"Right click", "Left click"}

    ' ── Projection ───────────────────────────────────────────────────────────

    Public Shared Function Describe(store As SettingsStore, running As Boolean) As Object
        Dim cfg = store.Settings.OrbWalker
        Dim problems = OrbWalkerSetup.Problems(store.Settings)
        Return New With {
            .enabled = running,
            .ready = problems.Count = 0,
            .problems = problems.ToArray(),
            .master = KeyOf(cfg.MasterKey),
            .toggle = cfg.Toggle,
            .hold = KeyOf(cfg.HoldKey),
            .press = KeyOf(cfg.PressKey),
            .delay = cfg.DelayMs,
            .clickDelay = cfg.ClickDelayMs,
            .delayMin = MinDelayMs,
            .delayMax = MaxDelayMs,
            .button = If(cfg.ClickRight, 0, 1),
            .buttons = ButtonNames,
            .cycle = DescribeCycle(cfg)
        }
    End Function

    Private Shared Function KeyOf(key As Integer) As Object
        Return New With {.text = SetupFields.DescribeKey(key), .isSet = key <> 0}
    End Function

    ''' <summary>The cycle in one line — "Hold Q · press A · wait 100 ms · right click · wait 100 ms".</summary>
    Private Shared Function DescribeCycle(cfg As OrbWalkerConfig) As String
        Dim name = Function(k As Integer) If(k = 0, "—", SetupFields.DescribeKey(k))
        Return $"Hold {name(cfg.HoldKey)} · press {name(cfg.PressKey)} · wait {cfg.DelayMs} ms · " &
               If(cfg.ClickRight, "right click", "left click") & $" · wait {cfg.ClickDelayMs} ms"
    End Function

    ' ── Validation ───────────────────────────────────────────────────────────

    ''' <summary>
    ''' Everything that stands between the settings and a module that can run, one sentence each,
    ''' in the order the screen lists the fields. Empty when there is nothing to fix. The board
    ''' itself is not asked here — that is a question for the moment the switch is thrown.
    ''' </summary>
    Public Shared Function Problems(settings As BotSettings) As List(Of String)
        Dim cfg = settings.OrbWalker
        Dim found As New List(Of String)()

        If Not settings.Input.ArduinoReady Then found.Add("Finish Setup — the Arduino is what presses the keys.")

        If cfg.MasterKey = 0 Then found.Add("Set the master key.")
        If cfg.HoldKey = 0 Then found.Add("Set the key to hold (step 1).")
        If cfg.PressKey = 0 Then found.Add("Set the key to press (step 2).")

        AddIfSame(found, cfg.MasterKey, cfg.HoldKey, "The master key and the key to hold")
        AddIfSame(found, cfg.MasterKey, cfg.PressKey, "The master key and the key to press")
        AddIfSame(found, cfg.HoldKey, cfg.PressKey, "The key to hold and the key to press")

        For Each key In {cfg.MasterKey, cfg.HoldKey, cfg.PressKey}
            If key <> 0 AndAlso Not SetupFields.IsPressableKey(key) Then
                found.Add($"{SetupFields.DescribeKey(key)} is not a key the board can press — set it again.")
            End If
        Next

        If cfg.DelayMs < MinDelayMs OrElse cfg.DelayMs > MaxDelayMs Then
            found.Add($"The delay after the press must be between {MinDelayMs} and {MaxDelayMs} ms.")
        End If
        If cfg.ClickDelayMs < MinDelayMs OrElse cfg.ClickDelayMs > MaxDelayMs Then
            found.Add($"The delay after the click must be between {MinDelayMs} and {MaxDelayMs} ms.")
        End If

        Return found
    End Function

    Private Shared Sub AddIfSame(found As List(Of String), a As Integer, b As Integer, which As String)
        If a <> 0 AndAlso a = b Then found.Add($"{which} are the same key — they must differ.")
    End Sub

    ' ── Edits ────────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Binds one of the three keys to what the capture dialog returned. Refused with a sentence
    ''' when it cannot work: a key the board has no press for, or one already doing another job.
    ''' </summary>
    Public Shared Sub SetKey(store As SettingsStore, slot As String, key As Keys)
        Dim cfg = store.Settings.OrbWalker
        Dim code = CInt(key) And &HFF
        If Not SetupFields.IsPressableKey(code) Then
            Throw New ArgumentException("That is not a key the bot can use — pick another one.")
        End If

        Dim others As New Dictionary(Of String, Integer) From {
            {SlotMaster, cfg.MasterKey}, {SlotHold, cfg.HoldKey}, {SlotPress, cfg.PressKey}}
        If Not others.ContainsKey(slot) Then Throw New ArgumentException($"Unknown key: {slot}")
        others.Remove(slot)
        For Each other In others
            If other.Value = code Then
                Throw New ArgumentException(
                    $"{SetupFields.DescribeKey(code)} is already {SlotName(other.Key)} — each key can do one job.")
            End If
        Next

        Select Case slot
            Case SlotMaster : cfg.MasterKey = code
            Case SlotHold : cfg.HoldKey = code
            Case SlotPress : cfg.PressKey = code
        End Select
        store.Save()
        Logger.Info("Orb Walker", $"{Capitalise(SlotName(slot))} set to {SetupFields.DescribeKey(code)}")
    End Sub

    ''' <summary>The toggle, the two delays and the click. Every field is sent every time; a delay is
    ''' clamped rather than refused, because a number past the range is a typo, not a decision.</summary>
    Public Shared Sub Save(store As SettingsStore, data As JObject)
        Dim cfg = store.Settings.OrbWalker
        cfg.Toggle = SetupFields.BoolOf(data, "toggle", cfg.Toggle)
        cfg.DelayMs = SetupFields.Clamp(SetupFields.IntOf(data, "delay", cfg.DelayMs), MinDelayMs, MaxDelayMs)
        cfg.ClickRight = SetupFields.Clamp(SetupFields.IntOf(data, "button", If(cfg.ClickRight, 0, 1)), 0, 1) = 0
        cfg.ClickDelayMs = SetupFields.Clamp(SetupFields.IntOf(data, "clickDelay", cfg.ClickDelayMs), MinDelayMs, MaxDelayMs)
        store.Save()
        Logger.Info("Orb Walker", $"{If(cfg.Toggle, "Toggle", "Hold")} mode — press, {cfg.DelayMs} ms, " &
                                  $"{If(cfg.ClickRight, "right", "left")} click, {cfg.ClickDelayMs} ms")
    End Sub

    Private Shared Function SlotName(slot As String) As String
        Select Case slot
            Case SlotMaster : Return "the master key"
            Case SlotHold : Return "the key to hold"
            Case Else : Return "the key to press"
        End Select
    End Function

    Private Shared Function Capitalise(text As String) As String
        If String.IsNullOrEmpty(text) Then Return text
        Return Char.ToUpperInvariant(text(0)) & text.Substring(1)
    End Function

End Class
