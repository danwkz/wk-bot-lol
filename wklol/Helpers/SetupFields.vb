Imports Newtonsoft.Json.Linq
Imports System.Windows.Forms

' ══════════════════════════════════════════════════════════════════════════════
' SetupFields  —  reading the page's named values, safely.
'
' NOTHING HERE TRUSTS THE PAGE. A missing field reads as the fallback, a field
' that is not a number reads as the fallback, and every caller clamps into the
' range the module can actually use. The engine reads these values on a
' background thread; what reaches it has to be a number this side chose.
'
' The page's min, max and disabled attributes are what a person clicking sees,
' and nothing else: every limit a screen shows is kept again here, and what a
' screen can only NAME — a key the native dialog captured — is checked against
' what exists rather than taken on trust.
' ══════════════════════════════════════════════════════════════════════════════

Friend NotInheritable Class SetupFields

    Private Sub New()
    End Sub

    Friend Shared Function IntOf(data As JObject, name As String, Optional fallback As Integer = 0) As Integer
        Dim token = data?(name)
        If token Is Nothing OrElse token.Type = JTokenType.Null Then Return fallback
        Dim value As Integer
        If Integer.TryParse(token.ToString(), value) Then Return value
        Return fallback
    End Function

    Friend Shared Function BoolOf(data As JObject, name As String, Optional fallback As Boolean = False) As Boolean
        Dim token = data?(name)
        If token Is Nothing OrElse token.Type = JTokenType.Null Then Return fallback
        Dim value As Boolean
        If Boolean.TryParse(token.ToString(), value) Then Return value
        Return fallback
    End Function

    Friend Shared Function TextOf(data As JObject, name As String) As String
        Dim token = data?(name)
        If token Is Nothing OrElse token.Type = JTokenType.Null Then Return String.Empty
        Return token.ToString().Trim()
    End Function

    Friend Shared Function Clamp(value As Integer, low As Integer, high As Integer) As Integer
        Return Math.Max(low, Math.Min(high, value))
    End Function

    ''' <summary>
    ''' Whether a key is one the capture dialog can produce and the board can press: a keyboard
    ''' code, never a mouse button (a key-down cannot report one) and nothing past the last
    ''' virtual-key code.
    ''' </summary>
    Friend Shared Function IsPressableKey(key As Integer) As Boolean
        If key <= 0 OrElse key > &HFF Then Return False
        Select Case CType(key, Keys)
            Case Keys.LButton, Keys.RButton, Keys.MButton, Keys.XButton1, Keys.XButton2
                Return False
        End Select
        Return True
    End Function

    ''' <summary>A key in words, or "" when nothing is bound.</summary>
    Friend Shared Function DescribeKey(key As Integer) As String
        If key = 0 Then Return String.Empty
        Return CType(key, Keys).ToString()
    End Function

End Class
