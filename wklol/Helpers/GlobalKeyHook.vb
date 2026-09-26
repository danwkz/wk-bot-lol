Imports System.Runtime.InteropServices
Imports System.Windows.Forms

' ============================================================
'  GlobalKeyHook.vb  -  System-wide low-level keyboard hook
'  Fires one edge-triggered event when a configured key goes
'  down anywhere — even while another application (the game)
'  is focused — and another when it comes back up. The key is
'  swallowed while bound, so it does not also reach the
'  focused window.
' ============================================================

Public NotInheritable Class GlobalKeyHook
    Implements IDisposable

    Private Const WH_KEYBOARD_LL As Integer = 13
    Private Const WM_KEYDOWN As Integer = &H100
    Private Const WM_KEYUP As Integer = &H101
    Private Const WM_SYSKEYDOWN As Integer = &H104
    Private Const WM_SYSKEYUP As Integer = &H105

    <StructLayout(LayoutKind.Sequential)>
    Private Structure KBDLLHOOKSTRUCT
        Public vkCode As UInteger
        Public scanCode As UInteger
        Public flags As UInteger
        Public time As UInteger
        Public dwExtraInfo As UIntPtr
    End Structure

    Private Delegate Function LowLevelKeyboardProc(nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function SetWindowsHookEx(idHook As Integer, lpfn As LowLevelKeyboardProc, hMod As IntPtr, dwThreadId As UInteger) As IntPtr
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function UnhookWindowsHookEx(hhk As IntPtr) As Boolean
    End Function

    <DllImport("user32.dll")>
    Private Shared Function CallNextHookEx(hhk As IntPtr, nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
    End Function

    ' Keep a reference so the GC never collects the callback while the hook is live.
    Private ReadOnly _proc As LowLevelKeyboardProc
    Private _hookId As IntPtr = IntPtr.Zero
    Private _isHeld As Boolean = False
    Private _disposed As Boolean = False

    ''' <summary>Raised once per physical press of the key, on the thread that installed the hook.</summary>
    Public Event HotkeyPressed As EventHandler

    ''' <summary>Raised when the key that raised <see cref="HotkeyPressed"/> comes back up.</summary>
    Public Event HotkeyReleased As EventHandler

    ''' <summary>
    ''' Virtual key (System.Windows.Forms.Keys) that triggers the hotkey. Keys.None = unbound.
    '''
    ''' A key alone, with no modifier to match: the key is HELD in the middle of a game, and a
    ''' binding that stopped answering because the player happened to be holding Shift would be a
    ''' binding that works most of the time.
    ''' </summary>
    Public Property TriggerKey As Keys = Keys.None

    Public Sub New()
        _proc = AddressOf HookCallback
    End Sub

    ''' <summary>
    ''' Updates the bound key (safe to call while installed). A key that was held under the old
    ''' binding is released first, so whoever listens is never left waiting for an up that will
    ''' not come. Binding the key that is already bound changes nothing — a run in progress goes on.
    ''' </summary>
    Public Sub SetBinding(key As Keys)
        If key = TriggerKey Then Return
        If _isHeld Then
            _isHeld = False
            RaiseSafely(False)
        End If
        TriggerKey = key
    End Sub

    Public ReadOnly Property IsInstalled As Boolean
        Get
            Return _hookId <> IntPtr.Zero
        End Get
    End Property

    ''' <summary>Installs the global hook. Must be called from a thread with a message pump (the UI thread).</summary>
    Public Function Install() As Boolean
        If _hookId <> IntPtr.Zero Then Return True
        ' A managed module handle is accepted by WH_KEYBOARD_LL; the module need not own the code.
        Dim hMod As IntPtr = Marshal.GetHINSTANCE(GetType(GlobalKeyHook).Module)
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0UI)
        Return _hookId <> IntPtr.Zero
    End Function

    Public Sub Uninstall()
        If _hookId = IntPtr.Zero Then Return
        UnhookWindowsHookEx(_hookId)
        _hookId = IntPtr.Zero
        If _isHeld Then
            _isHeld = False
            RaiseSafely(False)
        End If
    End Sub

    Private Function HookCallback(nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
        If nCode < 0 OrElse TriggerKey = Keys.None Then
            Return CallNextHookEx(_hookId, nCode, wParam, lParam)
        End If

        Dim msg As Integer = wParam.ToInt32()
        Dim info As KBDLLHOOKSTRUCT = CType(Marshal.PtrToStructure(lParam, GetType(KBDLLHOOKSTRUCT)), KBDLLHOOKSTRUCT)

        If Generic(CType(CInt(info.vkCode), Keys)) = TriggerKey Then
            Dim isDown As Boolean = (msg = WM_KEYDOWN OrElse msg = WM_SYSKEYDOWN)
            Dim isUp As Boolean = (msg = WM_KEYUP OrElse msg = WM_SYSKEYUP)

            If isDown Then
                If Not _isHeld Then
                    _isHeld = True
                    RaiseSafely(True)
                End If
                ' Swallow the press and its auto-repeat, so the bind does not leak into the game.
                Return New IntPtr(1)
            ElseIf isUp AndAlso _isHeld Then
                _isHeld = False
                RaiseSafely(False)
                Return New IntPtr(1)
            End If
        End If

        Return CallNextHookEx(_hookId, nCode, wParam, lParam)
    End Function

    ''' <summary>
    ''' The low-level hook reports WHICH Shift, Ctrl or Alt went down (VK_LSHIFT, VK_RMENU, …),
    ''' while a key captured from a form arrives as the generic one (VK_SHIFT, VK_MENU). Folded
    ''' here, or a modifier bound as the trigger would never match.
    ''' </summary>
    Private Shared Function Generic(vk As Keys) As Keys
        Select Case vk
            Case Keys.LShiftKey, Keys.RShiftKey : Return Keys.ShiftKey
            Case Keys.LControlKey, Keys.RControlKey : Return Keys.ControlKey
            Case Keys.LMenu, Keys.RMenu : Return Keys.Menu
            Case Else : Return vk
        End Select
    End Function

    ''' <summary>
    ''' Raises the edge. A listener that throws must not take the hook with it: Windows removes a
    ''' low-level hook that fails or stalls, silently, and the key would stop answering.
    ''' </summary>
    Private Sub RaiseSafely(pressed As Boolean)
        Try
            If pressed Then
                RaiseEvent HotkeyPressed(Me, EventArgs.Empty)
            Else
                RaiseEvent HotkeyReleased(Me, EventArgs.Empty)
            End If
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        Uninstall()
    End Sub

End Class
