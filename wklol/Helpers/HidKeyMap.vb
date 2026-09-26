Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

''' <summary>
''' Turns the virtual-key codes the bot works in into the USB HID usage ids the Arduino speaks.
'''
''' The two are NOT the same numbering and the difference is not cosmetic. A virtual key is what
''' Windows produces AFTER applying the keyboard layout; a HID usage is the physical key that was
''' pressed, before any layout exists. Send the wrong one and the game reads a different key —
''' silently, because every value is valid.
'''
''' The conversion therefore goes through the SCAN CODE rather than through a hand-written
''' VK-to-usage table:
''' <code>
'''   VK ──MapVirtualKeyEx(layout)──▶ scan code ──table──▶ HID usage
''' </code>
''' and the layout it asks is the one of the window the keys are going to. That closes the loop:
''' Windows converts the usage back into a virtual key on the way in, using that same layout, so
''' whatever the bot asked for is what the game receives.
'''
''' The direct table below is only the fallback, for the handful of virtual keys Windows has no
''' scan code for.
''' </summary>
Public NotInheritable Class HidKeyMap

    Private Sub New()
    End Sub

    ''' <summary>Returned when a key cannot be expressed as a HID usage at all.</summary>
    Public Const NoUsage As Byte = 0

#Region "Win32"

    Private Const MAPVK_VK_TO_VSC_EX As UInteger = 4UI

    <DllImport("user32.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function MapVirtualKeyExW(uCode As UInteger, uMapType As UInteger, dwhkl As IntPtr) As UInteger
    End Function

    <DllImport("user32.dll")>
    Private Shared Function GetKeyboardLayout(idThread As UInteger) As IntPtr
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function GetWindowThreadProcessId(hWnd As IntPtr, ByRef lpdwProcessId As UInteger) As UInteger
    End Function

#End Region

    ''' <summary>
    ''' The keyboard layout <paramref name="targetHwnd"/> is typing in, or our own when that cannot
    ''' be established. Never throws — a layout is a refinement, and falling back to ours is right
    ''' on every machine that uses one layout for everything.
    ''' </summary>
    Public Shared Function LayoutFor(targetHwnd As IntPtr) As IntPtr
        Try
            If targetHwnd <> IntPtr.Zero Then
                Dim pid As UInteger = 0UI
                Dim tid As UInteger = GetWindowThreadProcessId(targetHwnd, pid)
                If tid <> 0UI Then
                    Dim hkl As IntPtr = GetKeyboardLayout(tid)
                    If hkl <> IntPtr.Zero Then Return hkl
                End If
            End If
        Catch
            ' Fall through to our own layout.
        End Try
        Return GetKeyboardLayout(0UI)
    End Function

    ''' <summary>
    ''' HID usage id for a virtual key, or <see cref="NoUsage"/> when the key has none.
    ''' </summary>
    Public Shared Function UsageFor(key As Keys, layout As IntPtr) As Byte
        ' Strip the modifier bits Keys carries in its high bits.
        Dim vk As Integer = CInt(key) And &HFF
        If vk = 0 Then Return NoUsage

        Dim scan As UInteger = MapVirtualKeyExW(CUInt(vk), MAPVK_VK_TO_VSC_EX, layout)
        If scan <> 0UI Then
            ' The EX in MAPVK_VK_TO_VSC_EX is supposed to put the &HE0 prefix in the high byte for
            ' the extended keys. Measured in wk: for VK_LEFT it does not — it returns a bare &H4B,
            ' which is the scan code the LEFT ARROW SHARES WITH NUMPAD 4. So the prefix is not
            ' asked for, it is asserted, from the one list that decides which keys are extended.
            If IsExtendedKey(CType(vk, Keys)) Then scan = (scan And &HFFUI) Or &HE000UI

            Dim usage As Byte = UsageForScanCode(scan)
            If usage <> NoUsage Then Return usage
        End If

        ' No scan code, or one this table does not carry. The direct map covers the keys that
        ' matter and are position-invariant anyway.
        Dim direct As Byte = NoUsage
        If DirectMap.TryGetValue(vk, direct) Then Return direct
        Return NoUsage
    End Function

    ''' <summary>
    ''' Keys whose scan code carries the &amp;HE0 prefix — the navigation cluster, the right-hand
    ''' modifiers, the numpad divide. They share their low byte with a numpad key (End and Numpad1
    ''' are both &amp;H4F), and the prefix is what tells them apart.
    ''' </summary>
    Public Shared Function IsExtendedKey(key As Keys) As Boolean
        Select Case key
            Case Keys.Insert, Keys.Delete, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
                 Keys.Left, Keys.Up, Keys.Right, Keys.Down,
                 Keys.NumLock, Keys.Divide, Keys.RControlKey, Keys.RMenu,
                 Keys.Apps, Keys.LWin, Keys.RWin, Keys.PrintScreen, Keys.Cancel
                Return True
            Case Else
                Return False
        End Select
    End Function

    ''' <summary>
    ''' Scan code (set 1, with the &amp;HE0 / &amp;HE1 prefix in the high byte) to HID usage.
    ''' </summary>
    Private Shared Function UsageForScanCode(scan As UInteger) As Byte
        Dim usage As Byte = NoUsage
        If ExtendedMap.TryGetValue(CInt(scan), usage) Then Return usage
        If ScanMap.TryGetValue(CInt(scan And &HFFUI), usage) Then Return usage
        Return NoUsage
    End Function

    ''' <summary>
    ''' Set-1 scan codes with no prefix. The values are the USB HID Keyboard/Keypad page
    ''' (usage page 7) as published in the HID Usage Tables.
    ''' </summary>
    Private Shared ReadOnly ScanMap As New Dictionary(Of Integer, Byte) From {
        {&H1, &H29}, {&H2, &H1E}, {&H3, &H1F}, {&H4, &H20}, {&H5, &H21}, {&H6, &H22},
        {&H7, &H23}, {&H8, &H24}, {&H9, &H25}, {&HA, &H26}, {&HB, &H27}, {&HC, &H2D},
        {&HD, &H2E}, {&HE, &H2A}, {&HF, &H2B},
        {&H10, &H14}, {&H11, &H1A}, {&H12, &H8}, {&H13, &H15}, {&H14, &H17}, {&H15, &H1C},
        {&H16, &H18}, {&H17, &HC}, {&H18, &H12}, {&H19, &H13}, {&H1A, &H2F}, {&H1B, &H30},
        {&H1C, &H28}, {&H1D, &HE0},
        {&H1E, &H4}, {&H1F, &H16}, {&H20, &H7}, {&H21, &H9}, {&H22, &HA}, {&H23, &HB},
        {&H24, &HD}, {&H25, &HE}, {&H26, &HF}, {&H27, &H33}, {&H28, &H34}, {&H29, &H35},
        {&H2A, &HE1}, {&H2B, &H31},
        {&H2C, &H1D}, {&H2D, &H1B}, {&H2E, &H6}, {&H2F, &H19}, {&H30, &H5}, {&H31, &H11},
        {&H32, &H10}, {&H33, &H36}, {&H34, &H37}, {&H35, &H38}, {&H36, &HE5}, {&H37, &H55},
        {&H38, &HE2}, {&H39, &H2C}, {&H3A, &H39},
        {&H3B, &H3A}, {&H3C, &H3B}, {&H3D, &H3C}, {&H3E, &H3D}, {&H3F, &H3E}, {&H40, &H3F},
        {&H41, &H40}, {&H42, &H41}, {&H43, &H42}, {&H44, &H43},
        {&H45, &H53}, {&H46, &H47},
        {&H47, &H5F}, {&H48, &H60}, {&H49, &H61}, {&H4A, &H56},
        {&H4B, &H5C}, {&H4C, &H5D}, {&H4D, &H5E}, {&H4E, &H57},
        {&H4F, &H59}, {&H50, &H5A}, {&H51, &H5B}, {&H52, &H62}, {&H53, &H63},
        {&H56, &H64}, {&H57, &H44}, {&H58, &H45},
        {&H64, &H68}, {&H65, &H69}, {&H66, &H6A}, {&H67, &H6B},
        {&H68, &H6C}, {&H69, &H6D}, {&H6A, &H6E}, {&H6B, &H6F},
        {&H6C, &H70}, {&H6D, &H71}, {&H6E, &H72}, {&H76, &H73},
        {&H70, &H88}, {&H73, &H87}, {&H79, &H8A}, {&H7B, &H8B}, {&H7D, &H89}, {&H7E, &H85}
    }

    ''' <summary>
    ''' Scan codes carrying the &amp;HE0 prefix — the navigation cluster, the right-hand
    ''' modifiers, the numpad divide and Enter.
    ''' </summary>
    Private Shared ReadOnly ExtendedMap As New Dictionary(Of Integer, Byte) From {
        {&HE01C, &H58}, {&HE01D, &HE4}, {&HE035, &H54}, {&HE038, &HE6},
        {&HE047, &H4A}, {&HE048, &H52}, {&HE049, &H4B},
        {&HE04B, &H50}, {&HE04D, &H4F},
        {&HE04F, &H4D}, {&HE050, &H51}, {&HE051, &H4E},
        {&HE052, &H49}, {&HE053, &H4C},
        {&HE05B, &HE3}, {&HE05C, &HE7}, {&HE05D, &H65},
        {&HE037, &H46}, {&HE046, &H48}
    }

    ''' <summary>
    ''' Virtual keys Windows gives no scan code for. Kept small on purpose — everything a real
    ''' keyboard has goes through the scan-code path.
    ''' </summary>
    Private Shared ReadOnly DirectMap As New Dictionary(Of Integer, Byte) From {
        {CInt(Keys.ControlKey), &HE0},
        {CInt(Keys.ShiftKey), &HE1},
        {CInt(Keys.Menu), &HE2},
        {CInt(Keys.Enter), &H28},
        {CInt(Keys.Escape), &H29},
        {CInt(Keys.Space), &H2C},
        {CInt(Keys.Tab), &H2B},
        {CInt(Keys.Back), &H2A}
    }

End Class
