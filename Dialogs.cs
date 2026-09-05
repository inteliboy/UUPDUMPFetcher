using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Xml;

namespace UupDumpFetcher
{
    // =========================================================================
    //  FlatProgressBar  -  owner-drawn progress bar
    //
    //  A native ProgressBar always paints its fill in the current Windows theme
    //  color (green) under Application.EnableVisualStyles() regardless of
    //  ForeColor, so it can never actually render this app's blue accent.
    //  Drop-in replacement for the two properties this app actually used:
    //  Value (0-100) and a marquee sweep mode.
    // =========================================================================
    class FlatProgressBar : Control
    {
        int _value;
        bool _marquee;
        double _marqueePos = 0;
        int _marqueeDir = 1;
        System.Windows.Forms.Timer _marqueeTimer;

        public Color BarColor = Theme.Accent;

        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public bool Marquee
        {
            get { return _marquee; }
            set
            {
                if (_marquee == value) return;
                _marquee = value;
                if (_marquee)
                {
                    if (_marqueeTimer == null)
                    {
                        _marqueeTimer = new System.Windows.Forms.Timer();
                        _marqueeTimer.Interval = 20;
                        _marqueeTimer.Tick += delegate(object s, EventArgs e) { TickMarquee(); };
                    }
                    _marqueeTimer.Start();
                }
                else
                {
                    if (_marqueeTimer != null) _marqueeTimer.Stop();
                    _marqueePos = 0;
                    Invalidate();
                }
            }
        }

        public FlatProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                      ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 18;
        }

        void TickMarquee()
        {
            int blockW = Math.Max(30, Width / 5);
            double maxPos = Math.Max(1, Width - blockW);
            _marqueePos += _marqueeDir * 4;
            if (_marqueePos >= maxPos) { _marqueePos = maxPos; _marqueeDir = -1; }
            else if (_marqueePos <= 0) { _marqueePos = 0; _marqueeDir = 1; }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            if (_marquee)
            {
                int blockW = Math.Max(30, Width / 5);
                using (SolidBrush b = new SolidBrush(BarColor))
                    g.FillRectangle(b, (int)_marqueePos + 1, 1, blockW, Math.Max(0, Height - 2));
            }
            else if (_value > 0)
            {
                int w = (int)((Width - 2) * (_value / 100.0));
                if (w > 0)
                    using (SolidBrush b = new SolidBrush(BarColor))
                        g.FillRectangle(b, 1, 1, w, Math.Max(0, Height - 2));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _marqueeTimer != null) { _marqueeTimer.Dispose(); _marqueeTimer = null; }
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    //  ThemedMessageBox  -  drop-in MessageBox.Show replacement
    //
    //  The default button (whichever Enter triggers) is accent blue, "No" is
    //  red, "Cancel" is orange, every other button stays neutral - matches
    //  this app's one accent color used everywhere else (progress bars,
    //  focus), rather than introducing a second "positive" color.
    // =========================================================================
    static class ThemedMessageBox
    {
        public static DialogResult Show(string text, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (Form f = new Form())
            {
                f.Text = caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = Theme.Bg;
                f.Font = Ctrl.SegoUI();
                f.AutoScaleMode = AutoScaleMode.None;

                Icon sysIcon = IconFor(icon);
                int picBottom = 0;
                if (sysIcon != null)
                {
                    PictureBox pic = new PictureBox();
                    pic.Image = sysIcon.ToBitmap();
                    pic.SizeMode = PictureBoxSizeMode.AutoSize;
                    pic.Location = new Point(20, 20);
                    f.Controls.Add(pic);
                    picBottom = pic.Bottom;
                }
                int textLeft = sysIcon != null ? 66 : 20;

                Label lbl = new Label();
                lbl.Text = text;
                lbl.Font = Ctrl.SegoUI();
                lbl.ForeColor = Theme.Fg;
                lbl.BackColor = Color.Transparent;
                lbl.AutoSize = true;
                lbl.MaximumSize = new Size(360, 0);
                lbl.Location = new Point(textLeft, 22);
                f.Controls.Add(lbl);

                int contentBottom = Math.Max(lbl.Bottom, picBottom) + 24;
                int clientWidth = Math.Max(360, textLeft + lbl.Width + 20);

                string[] labels; DialogResult[] results;
                ButtonSpecsFor(buttons, out labels, out results);

                int btnH = 30, gap = 10, btnBottomMargin = 16;
                int totalBtnW = 0;
                Button[] btns = new Button[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    Button b = new Button();
                    b.Text = labels[i];
                    b.Font = Ctrl.SegoUI();
                    b.FlatStyle = FlatStyle.Flat;
                    b.Height = btnH;
                    b.Width = TextRenderer.MeasureText(labels[i], b.Font).Width + 30;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = Theme.Border;
                    b.Cursor = Cursors.Hand;

                    bool isDefault = i == 0;
                    bool isNo = results[i] == DialogResult.No;
                    bool isCancel = results[i] == DialogResult.Cancel;

                    if (isNo) { b.BackColor = Theme.Err; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; }
                    else if (isCancel) { b.BackColor = Theme.Warn; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; }
                    else if (isDefault) { b.BackColor = Theme.Accent; b.ForeColor = Theme.AccentFg; b.FlatAppearance.BorderSize = 0; }
                    else { b.BackColor = Theme.Surface; b.ForeColor = Theme.Fg; }

                    DialogResult res = results[i];
                    b.Click += delegate(object s, EventArgs e) { f.Tag = res; f.DialogResult = res; f.Close(); };

                    btns[i] = b;
                    totalBtnW += b.Width + gap;
                }
                totalBtnW -= gap;

                clientWidth = Math.Max(clientWidth, totalBtnW + 40);
                int x = clientWidth - totalBtnW - 20;
                foreach (Button b in btns)
                {
                    b.Location = new Point(x, contentBottom);
                    f.Controls.Add(b);
                    x += b.Width + gap;
                }

                f.ClientSize = new Size(clientWidth, contentBottom + btnH + btnBottomMargin);
                f.AcceptButton = btns[0];
                // Escape maps to Cancel if present, else the last (least
                // affirmative) button - mirrors standard MessageBox behavior.
                Button cancelBtn = null;
                for (int i = 0; i < results.Length; i++) if (results[i] == DialogResult.Cancel) cancelBtn = btns[i];
                f.CancelButton = cancelBtn != null ? cancelBtn : btns[btns.Length - 1];

                DialogResult dr = f.ShowDialog();
                return dr;
            }
        }

        static Icon IconFor(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Error: return SystemIcons.Error;
                case MessageBoxIcon.Warning: return SystemIcons.Warning;
                case MessageBoxIcon.Information: return SystemIcons.Information;
                case MessageBoxIcon.Question: return SystemIcons.Question;
                default: return null;
            }
        }

        static void ButtonSpecsFor(MessageBoxButtons buttons, out string[] labels, out DialogResult[] results)
        {
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel:
                    labels = new string[] { "OK", "Cancel" };
                    results = new DialogResult[] { DialogResult.OK, DialogResult.Cancel };
                    break;
                case MessageBoxButtons.YesNo:
                    labels = new string[] { "Yes", "No" };
                    results = new DialogResult[] { DialogResult.Yes, DialogResult.No };
                    break;
                case MessageBoxButtons.YesNoCancel:
                    labels = new string[] { "Yes", "No", "Cancel" };
                    results = new DialogResult[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel };
                    break;
                case MessageBoxButtons.RetryCancel:
                    labels = new string[] { "Retry", "Cancel" };
                    results = new DialogResult[] { DialogResult.Retry, DialogResult.Cancel };
                    break;
                case MessageBoxButtons.AbortRetryIgnore:
                    labels = new string[] { "Abort", "Retry", "Ignore" };
                    results = new DialogResult[] { DialogResult.Abort, DialogResult.Retry, DialogResult.Ignore };
                    break;
                default:
                    labels = new string[] { "OK" };
                    results = new DialogResult[] { DialogResult.OK };
                    break;
            }
        }
    }
}
