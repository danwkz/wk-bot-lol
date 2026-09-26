Imports System.IO
Imports System.Windows.Forms
Imports Newtonsoft.Json
Imports wklol.Core

' ══════════════════════════════════════════════════════════════════════════════
' BotSettings  —  everything the bot keeps between sessions.
'
' ONE FILE, SAVED ON EVERY EDIT. wk keeps named profiles and writes them only on
' Save; that exists because a profile belongs to one of many characters. This
' bot has one board and one module, so there is one file beside the executable
' and every change is on disk by the time its screen repaints — there is nothing
' to lose by closing.
'
' Newtonsoft ignores unknown keys, so a node added later reads an older file
' fine: the missing node keeps its defaults.
' ══════════════════════════════════════════════════════════════════════════════

Public Class BotSettings

    ''' <summary>The board and how long a key or a button stays down.</summary>
    Public Property Input As New InputConfig()

    ''' <summary>The Orb Walker's keys and timing.</summary>
    Public Property OrbWalker As New OrbWalkerConfig()

End Class

' ──────────────────────────────────────────────────────────────────────────────
' InputConfig  —  the board, and the timing of every key and click it makes.
' ──────────────────────────────────────────────────────────────────────────────

Public Class InputConfig

    ''' <summary>
    ''' COM port of the Arduino, or empty for "find it".
    '''
    ''' Empty is the right default and is what most setups should stay on: the bot opens each port
    ''' in turn and keeps the one that answers the firmware handshake, so the board is found again
    ''' after Windows renumbers it — which it does whenever it is plugged into a different socket,
    ''' and every time it is flashed. Pinning a port is for the machine that has several serial
    ''' devices and where opening the others is unwelcome.
    ''' </summary>
    Public Property ArduinoPort As String = ""

    ''' <summary>
    ''' Whether Setup has been walked to its end at least once: a board found, flashed, and checked
    ''' end to end. It is the gate the rest of the interface opens on. It says the board WAS ready,
    ''' not that it is plugged in now — switching a module on asks the board itself.
    ''' </summary>
    Public Property ArduinoReady As Boolean = False

    ''' <summary>
    ''' How long a pressed key stays down, in milliseconds. A keystroke whose down and up arrive
    ''' together is routinely dropped by a game that samples input once per frame, so the hold has
    ''' to outlast a frame.
    ''' </summary>
    Public Property KeyHoldMin As Integer = 25
    Public Property KeyHoldMax As Integer = 45

    ''' <summary>How long a clicked button stays down, in milliseconds. Same reasoning as the keys.</summary>
    Public Property ClickHoldMin As Integer = 25
    Public Property ClickHoldMax As Integer = 45

End Class

' ──────────────────────────────────────────────────────────────────────────────
' OrbWalkerConfig  —  the Orb Walker's keys and timing.
' ──────────────────────────────────────────────────────────────────────────────

''' <summary>
''' The cycle, in the order it runs: while <see cref="MasterKey"/> is held, <see cref="HoldKey"/> is
''' kept down, and <see cref="PressKey"/> is tapped, <see cref="DelayMs"/> waited, the mouse clicked
''' at the cursor and <see cref="ClickDelayMs"/> waited, over and over — the press and the click in
''' turn, each followed by its own wait. Keys are virtual-key codes (System.Windows.Forms.Keys); 0 is
''' "not set".
''' </summary>
Public Class OrbWalkerConfig

    ''' <summary>The global hotkey. Holding it runs the cycle; letting go ends it — or, with
    ''' <see cref="Toggle"/> on, one press starts it and the next one ends it.</summary>
    Public Property MasterKey As Integer = 0

    ''' <summary>Whether the master key is a toggle (press to start, press again to stop) rather
    ''' than a key to hold.</summary>
    Public Property Toggle As Boolean = False

    ''' <summary>Held down for as long as the master key is (step 1).</summary>
    Public Property HoldKey As Integer = 0

    ''' <summary>Tapped once per cycle (step 2).</summary>
    Public Property PressKey As Integer = 0

    ''' <summary>The wait between the tap and the click, in milliseconds (step 3).</summary>
    Public Property DelayMs As Integer = 100

    ''' <summary>Which button the click at the cursor uses (step 4). Right is the move command in
    ''' League of Legends.</summary>
    Public Property ClickRight As Boolean = True

    ''' <summary>The wait between the click and the next tap, in milliseconds (step 5).</summary>
    Public Property ClickDelayMs As Integer = 100

End Class

' ──────────────────────────────────────────────────────────────────────────────
' SettingsStore  —  where BotSettings lives on disk.
' ──────────────────────────────────────────────────────────────────────────────

Public NotInheritable Class SettingsStore

    Public Shared ReadOnly Property FilePath As String
        Get
            Return Path.Combine(Application.StartupPath, "settings.json")
        End Get
    End Property

    ''' <summary>The live settings. Edited on the UI thread; a module is handed copies of what it
    ''' needs when it is configured, never this object.</summary>
    Public ReadOnly Property Settings As BotSettings

    Public Sub New()
        Settings = Load()
    End Sub

    Private Shared Function Load() As BotSettings
        If Not File.Exists(FilePath) Then Return New BotSettings()
        Try
            Dim loaded = JsonConvert.DeserializeObject(Of BotSettings)(File.ReadAllText(FilePath))
            If loaded IsNot Nothing Then
                ' A node written as null, or hand-edited away, keeps its defaults rather than
                ' becoming a Nothing every reader would have to guard against.
                If loaded.Input Is Nothing Then loaded.Input = New InputConfig()
                If loaded.OrbWalker Is Nothing Then loaded.OrbWalker = New OrbWalkerConfig()
                Return loaded
            End If
        Catch ex As Exception
            Logger.Warn("Settings", $"Could not read {FilePath} — starting from the defaults: {ex.Message}")
        End Try
        Return New BotSettings()
    End Function

    ''' <summary>Writes the settings. Never throws: a file that cannot be written costs the next
    ''' session its settings, and says so in the log, but must not cost this one its input.</summary>
    Public Sub Save()
        Try
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(Settings, Formatting.Indented))
        Catch ex As Exception
            Logger.Warn("Settings", $"Could not save {FilePath}: {ex.Message}")
        End Try
    End Sub

End Class
