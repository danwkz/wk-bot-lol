<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class FrmKeyCapture

    Private components As System.ComponentModel.IContainer

    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    ' ── Controls declared so the .vb file can reference them ─
    Friend WithEvents pnlHeader As System.Windows.Forms.Panel
    Friend WithEvents pnlBody As System.Windows.Forms.TableLayoutPanel
    Friend WithEvents pnlIconWrap As System.Windows.Forms.Panel
    Friend WithEvents iconHolder As System.Windows.Forms.Panel
    Friend WithEvents lblTitle As System.Windows.Forms.Label
    Friend WithEvents lblSubtitle As System.Windows.Forms.Label
    Friend WithEvents pnlCapture As System.Windows.Forms.Panel
    Friend WithEvents lblHint As System.Windows.Forms.Label
    Friend WithEvents pnlCancelRow As System.Windows.Forms.Panel
    Friend WithEvents btnCancel As System.Windows.Forms.Button
    Friend WithEvents dot As System.Windows.Forms.Panel
    Friend WithEvents lblHeader As System.Windows.Forms.Label

    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()

        ' ── Instantiate ────────────────────────────────────────
        Me.pnlHeader = New System.Windows.Forms.Panel()
        Me.dot = New System.Windows.Forms.Panel()
        Me.lblHeader = New System.Windows.Forms.Label()
        Me.pnlBody = New System.Windows.Forms.TableLayoutPanel()
        Me.pnlIconWrap = New System.Windows.Forms.Panel()
        Me.iconHolder = New System.Windows.Forms.Panel()
        Me.lblTitle = New System.Windows.Forms.Label()
        Me.lblSubtitle = New System.Windows.Forms.Label()
        Me.pnlCapture = New System.Windows.Forms.Panel()
        Me.lblHint = New System.Windows.Forms.Label()
        Me.pnlCancelRow = New System.Windows.Forms.Panel()
        Me.btnCancel = New System.Windows.Forms.Button()

        Me.pnlHeader.SuspendLayout()
        Me.pnlBody.SuspendLayout()
        Me.pnlIconWrap.SuspendLayout()
        Me.pnlCapture.SuspendLayout()
        Me.pnlCancelRow.SuspendLayout()
        Me.SuspendLayout()

        ' ══════════════════════════════════════════════════════
        ' pnlHeader
        ' ══════════════════════════════════════════════════════
        Me.pnlHeader.BackColor = System.Drawing.Color.FromArgb(15, 34, 96)
        Me.pnlHeader.Dock = System.Windows.Forms.DockStyle.Top
        Me.pnlHeader.Height = 42
        Me.pnlHeader.Name = "pnlHeader"
        Me.pnlHeader.Controls.Add(Me.dot)
        Me.pnlHeader.Controls.Add(Me.lblHeader)

        ' dot
        Me.dot.BackColor = System.Drawing.Color.FromArgb(74, 144, 217)
        Me.dot.Location = New System.Drawing.Point(16, 17)
        Me.dot.Name = "dot"
        Me.dot.Size = New System.Drawing.Size(8, 8)
        Me.dot.Region = New System.Drawing.Region(New System.Drawing.Rectangle(0, 0, 8, 8))

        ' lblHeader
        Me.lblHeader.AutoSize = True
        Me.lblHeader.Font = New System.Drawing.Font("Segoe UI", 7.5!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point)
        Me.lblHeader.ForeColor = System.Drawing.Color.FromArgb(200, 214, 245)
        Me.lblHeader.Location = New System.Drawing.Point(32, 14)
        Me.lblHeader.Name = "lblHeader"
        Me.lblHeader.Text = "SET A KEY"

        ' ══════════════════════════════════════════════════════
        ' pnlBody  (TableLayoutPanel — 1 col × 5 rows)
        ' ══════════════════════════════════════════════════════
        Me.pnlBody.BackColor = System.Drawing.Color.White
        Me.pnlBody.ColumnCount = 1
        Me.pnlBody.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.pnlBody.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlBody.Name = "pnlBody"
        Me.pnlBody.Padding = New System.Windows.Forms.Padding(20, 20, 20, 14)
        Me.pnlBody.RowCount = 5
        Me.pnlBody.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize))
        Me.pnlBody.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize))
        Me.pnlBody.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize))
        Me.pnlBody.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize))
        Me.pnlBody.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize))
        Me.pnlBody.Controls.Add(Me.pnlIconWrap, 0, 0)
        Me.pnlBody.Controls.Add(Me.lblTitle, 0, 1)
        Me.pnlBody.Controls.Add(Me.lblSubtitle, 0, 2)
        Me.pnlBody.Controls.Add(Me.pnlCapture, 0, 3)
        Me.pnlBody.Controls.Add(Me.pnlCancelRow, 0, 4)

        ' ══════════════════════════════════════════════════════
        ' pnlIconWrap  (centering wrapper)
        ' ══════════════════════════════════════════════════════
        Me.pnlIconWrap.BackColor = System.Drawing.Color.White
        Me.pnlIconWrap.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlIconWrap.Height = 70
        Me.pnlIconWrap.Name = "pnlIconWrap"
        Me.pnlIconWrap.Controls.Add(Me.iconHolder)

        ' iconHolder
        Me.iconHolder.BackColor = System.Drawing.Color.FromArgb(238, 242, 255)
        Me.iconHolder.Location = New System.Drawing.Point(0, 8)
        Me.iconHolder.Name = "iconHolder"
        Me.iconHolder.Size = New System.Drawing.Size(54, 54)
        Me.iconHolder.Region = New System.Drawing.Region(New System.Drawing.Rectangle(0, 0, 54, 54))

        ' ══════════════════════════════════════════════════════
        ' lblTitle
        ' ══════════════════════════════════════════════════════
        Me.lblTitle.AutoSize = False
        Me.lblTitle.Dock = System.Windows.Forms.DockStyle.Fill
        Me.lblTitle.Font = New System.Drawing.Font("Segoe UI", 9.5!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point)
        Me.lblTitle.ForeColor = System.Drawing.Color.FromArgb(26, 42, 94)
        Me.lblTitle.Height = 22
        Me.lblTitle.Name = "lblTitle"
        Me.lblTitle.Text = "Press the key to use"
        Me.lblTitle.TextAlign = System.Drawing.ContentAlignment.MiddleCenter

        ' ══════════════════════════════════════════════════════
        ' lblSubtitle
        ' ══════════════════════════════════════════════════════
        Me.lblSubtitle.AutoSize = False
        Me.lblSubtitle.Dock = System.Windows.Forms.DockStyle.Fill
        Me.lblSubtitle.Font = New System.Drawing.Font("Segoe UI", 8.0!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point)
        Me.lblSubtitle.ForeColor = System.Drawing.Color.FromArgb(143, 160, 200)
        Me.lblSubtitle.Height = 38
        Me.lblSubtitle.Name = "lblSubtitle"
        Me.lblSubtitle.Text = "One key, on its own — the first" & Environment.NewLine & "one pressed is the one kept"
        Me.lblSubtitle.TextAlign = System.Drawing.ContentAlignment.MiddleCenter

        ' ══════════════════════════════════════════════════════
        ' pnlCapture
        ' ══════════════════════════════════════════════════════
        Me.pnlCapture.BackColor = System.Drawing.Color.FromArgb(244, 247, 255)
        Me.pnlCapture.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlCapture.Height = 48
        Me.pnlCapture.Name = "pnlCapture"
        Me.pnlCapture.Controls.Add(Me.lblHint)

        ' lblHint
        Me.lblHint.AutoEllipsis = True
        Me.lblHint.Dock = System.Windows.Forms.DockStyle.Fill
        Me.lblHint.Font = New System.Drawing.Font("Segoe UI", 9.0!, System.Drawing.FontStyle.Italic, System.Drawing.GraphicsUnit.Point)
        Me.lblHint.ForeColor = System.Drawing.Color.FromArgb(143, 160, 200)
        Me.lblHint.Name = "lblHint"
        Me.lblHint.Text = "Waiting for input..."
        Me.lblHint.TextAlign = System.Drawing.ContentAlignment.MiddleCenter

        ' ══════════════════════════════════════════════════════
        ' pnlCancelRow
        ' ══════════════════════════════════════════════════════
        Me.pnlCancelRow.BackColor = System.Drawing.Color.White
        Me.pnlCancelRow.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlCancelRow.Height = 32
        Me.pnlCancelRow.Name = "pnlCancelRow"
        Me.pnlCancelRow.Controls.Add(Me.btnCancel)

        ' btnCancel
        Me.btnCancel.Anchor = System.Windows.Forms.AnchorStyles.Right Or System.Windows.Forms.AnchorStyles.Bottom
        Me.btnCancel.AutoSize = True
        Me.btnCancel.BackColor = System.Drawing.Color.Transparent
        Me.btnCancel.Cursor = System.Windows.Forms.Cursors.Hand
        Me.btnCancel.FlatStyle = System.Windows.Forms.FlatStyle.Flat
        Me.btnCancel.FlatAppearance.BorderSize = 0
        Me.btnCancel.FlatAppearance.MouseOverBackColor = System.Drawing.Color.Transparent
        Me.btnCancel.Font = New System.Drawing.Font("Segoe UI", 8.0!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point)
        Me.btnCancel.ForeColor = System.Drawing.Color.FromArgb(143, 160, 200)
        Me.btnCancel.Location = New System.Drawing.Point(0, 4)
        Me.btnCancel.Name = "btnCancel"
        Me.btnCancel.Text = "Cancel"

        ' ══════════════════════════════════════════════════════
        ' FrmKeyCapture (form)
        ' ══════════════════════════════════════════════════════
        Me.AutoSize = False
        Me.BackColor = System.Drawing.Color.White
        Me.ClientSize = New System.Drawing.Size(340, 290)
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None
        Me.KeyPreview = True
        Me.MinimumSize = New System.Drawing.Size(300, 240)
        Me.Name = "FrmKeyCapture"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
        Me.Text = "Set a key"

        Me.Controls.Add(Me.pnlBody)
        Me.Controls.Add(Me.pnlHeader)   ' adicionado por último → fica no topo (DockStyle.Top)

        ' ── Wire up event handlers ─────────────────────────────
        AddHandler Me.iconHolder.Paint, AddressOf iconHolder_Paint
        AddHandler Me.pnlCapture.Paint, AddressOf pnlCapture_Paint
        AddHandler Me.pnlCapture.Resize, AddressOf pnlCapture_Resize
        AddHandler Me.pnlIconWrap.Resize, AddressOf pnlIconWrap_Resize
        AddHandler Me.pnlCancelRow.Resize, AddressOf pnlCancelRow_Resize
        AddHandler Me.btnCancel.Click, AddressOf btnCancel_Click

        Me.pnlHeader.ResumeLayout(False)
        Me.pnlHeader.PerformLayout()
        Me.pnlBody.ResumeLayout(False)
        Me.pnlIconWrap.ResumeLayout(False)
        Me.pnlCapture.ResumeLayout(False)
        Me.pnlCancelRow.ResumeLayout(False)
        Me.pnlCancelRow.PerformLayout()
        Me.ResumeLayout(False)

        ' ── Apply rounded regions after layout ─────────────────
        Me.iconHolder.Region = New System.Drawing.Region(
            New System.Drawing.Rectangle(0, 0, Me.iconHolder.Width, Me.iconHolder.Height))
        Me.pnlCapture.Region = New System.Drawing.Region(
            New System.Drawing.Rectangle(0, 0, Me.pnlCapture.Width, Me.pnlCapture.Height))

    End Sub

End Class