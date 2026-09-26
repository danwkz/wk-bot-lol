Public Class FrmKeyCapture
    Inherits System.Windows.Forms.Form

    ' ── Public property ─────────────────────────────────────
    Public Property CapturedKey As System.Windows.Forms.Keys

    ' ── Captured modifier state ──────────────────────────────
    Public Property CapturedCtrl As Boolean
    Public Property CapturedAlt As Boolean
    Public Property CapturedShift As Boolean

    ' ── Color palette ───────────────────────────────────────
    Private ReadOnly NavyDark As System.Drawing.Color = System.Drawing.Color.FromArgb(15, 34, 96)
    Private ReadOnly NavyLight As System.Drawing.Color = System.Drawing.Color.FromArgb(200, 214, 245)
    Private ReadOnly BorderBlue As System.Drawing.Color = System.Drawing.Color.FromArgb(208, 217, 245)

    ' ── Constructor ─────────────────────────────────────────
    Public Sub New()
        InitializeComponent()
    End Sub

    ' ── Icon paint handler ──────────────────────────────────
    Private Sub DrawKeyboardIcon(sender As Object, e As System.Windows.Forms.PaintEventArgs)
        Dim g As System.Drawing.Graphics = e.Graphics
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias
        Using pen As New System.Drawing.Pen(NavyDark, 1.6) With {.LineJoin = System.Drawing.Drawing2D.LineJoin.Round}
            g.DrawRectangle(pen, 8, 10, 7, 7)
            g.DrawRectangle(pen, 18, 10, 7, 7)
            g.DrawRectangle(pen, 28, 10, 14, 7)
            g.DrawRectangle(pen, 8, 21, 34, 6)
            g.DrawRectangle(pen, 8, 31, 14, 6)
            g.DrawRectangle(pen, 25, 31, 17, 6)
        End Using
    End Sub

    ' ── Rounded region helper ────────────────────────────────
    Private Function RoundedRegion(sz As System.Drawing.Size, radius As Integer) As System.Drawing.Region
        Dim path As New System.Drawing.Drawing2D.GraphicsPath()
        path.AddArc(0, 0, radius * 2, radius * 2, 180, 90)
        path.AddArc(sz.Width - radius * 2, 0, radius * 2, radius * 2, 270, 90)
        path.AddArc(sz.Width - radius * 2, sz.Height - radius * 2, radius * 2, radius * 2, 0, 90)
        path.AddArc(0, sz.Height - radius * 2, radius * 2, radius * 2, 90, 90)
        path.CloseFigure()
        Return New System.Drawing.Region(path)
    End Function

    ' ── Key capture ─────────────────────────────────────────
    Protected Overrides Sub OnKeyDown(e As System.Windows.Forms.KeyEventArgs)
        MyBase.OnKeyDown(e)
        CapturedKey = e.KeyCode
        CapturedCtrl = e.Control
        CapturedAlt = e.Alt
        CapturedShift = e.Shift

        Dim parts As New System.Collections.Generic.List(Of String)
        If e.Control Then parts.Add("Ctrl")
        If e.Alt Then parts.Add("Alt")
        If e.Shift Then parts.Add("Shift")
        If e.KeyCode <> System.Windows.Forms.Keys.ControlKey AndAlso
           e.KeyCode <> System.Windows.Forms.Keys.Menu AndAlso
           e.KeyCode <> System.Windows.Forms.Keys.ShiftKey Then
            parts.Add(e.KeyCode.ToString())
        End If

        lblHint.Text = String.Join(" + ", parts)
        lblHint.Font = New System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold)
        lblHint.ForeColor = NavyDark

        System.Windows.Forms.Application.DoEvents()
        System.Threading.Thread.Sleep(250)

        Me.DialogResult = System.Windows.Forms.DialogResult.OK
        Me.Close()
    End Sub

    ' ── Outer border paint ───────────────────────────────────
    Protected Overrides Sub OnPaint(e As System.Windows.Forms.PaintEventArgs)
        MyBase.OnPaint(e)
        Using pen As New System.Drawing.Pen(System.Drawing.Color.FromArgb(30, NavyDark), 1.5)
            pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset
            e.Graphics.DrawRectangle(pen, Me.ClientRectangle)
        End Using
    End Sub

    ' ── Icon holder paint (called from designer hookup) ──────
    Friend Sub iconHolder_Paint(sender As Object, e As System.Windows.Forms.PaintEventArgs)
        DrawKeyboardIcon(sender, e)
        Using pen As New System.Drawing.Pen(BorderBlue, 1)
            e.Graphics.DrawRectangle(pen, 0, 0,
                DirectCast(sender, System.Windows.Forms.Panel).Width - 1,
                DirectCast(sender, System.Windows.Forms.Panel).Height - 1)
        End Using
    End Sub

    ' ── Capture panel paint (called from designer hookup) ────
    Friend Sub pnlCapture_Paint(sender As Object, e As System.Windows.Forms.PaintEventArgs)
        Using pen As New System.Drawing.Pen(BorderBlue, 1)
            e.Graphics.DrawRectangle(pen, 0, 0, pnlCapture.Width - 1, pnlCapture.Height - 1)
        End Using
    End Sub

    ' ── Resize handlers ──────────────────────────────────────
    Friend Sub pnlCapture_Resize(sender As Object, e As System.EventArgs)
        pnlCapture.Region = RoundedRegion(pnlCapture.Size, 8)
    End Sub

    Friend Sub pnlIconWrap_Resize(sender As Object, e As System.EventArgs)
        iconHolder.Left = (pnlIconWrap.Width - 54) \ 2
        iconHolder.Top = 8
    End Sub

    Friend Sub pnlCancelRow_Resize(sender As Object, e As System.EventArgs)
        btnCancel.Left = pnlCancelRow.Width - btnCancel.Width
    End Sub

    ' ── Cancel button ────────────────────────────────────────
    Private Sub btnCancel_Click(sender As Object, e As System.EventArgs)
        Me.Close()
    End Sub

End Class