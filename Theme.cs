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
    //  Theme  (identical palette to Triquetra.cs + dark-mode registry detection)
    // =========================================================================
    static class Theme
    {
        public static bool IsDark;

        static Theme()
        {
            IsDark = DetectDarkMode();
        }

        static bool DetectDarkMode()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key == null) return true;
                object v = key.GetValue("AppsUseLightTheme");
                key.Close();
                return v == null || (int)v == 0;
            }
            catch { return true; }
        }

        public static Color Accent    { get { return Color.FromArgb(0x00, 0x78, 0xD4); } }
        public static Color AccentHov { get { return Color.FromArgb(0x10, 0x6E, 0xBE); } }
        public static Color AccentFg  { get { return Color.White; } }
        public static Color Bg        { get { return IsDark ? Color.FromArgb(0x1A,0x1A,0x2E) : Color.FromArgb(0xF3,0xF3,0xF8); } }
        public static Color Surface   { get { return IsDark ? Color.FromArgb(0x25,0x25,0x40) : Color.White; } }
        public static Color Border    { get { return IsDark ? Color.FromArgb(0x35,0x35,0x60) : Color.FromArgb(0xD0,0xD0,0xE0); } }
        public static Color Fg        { get { return IsDark ? Color.FromArgb(0xE8,0xE8,0xF0) : Color.FromArgb(0x1A,0x1A,0x2E); } }
        public static Color FgDim     { get { return IsDark ? Color.FromArgb(0x88,0x88,0xAA) : Color.FromArgb(0x60,0x60,0x7A); } }
        public static Color Ok        { get { return IsDark ? Color.FromArgb(0x6C,0xCB,0x6C) : Color.FromArgb(0x21,0x7A,0x21); } }
        public static Color Warn      { get { return IsDark ? Color.FromArgb(0xF0,0xA0,0x50) : Color.FromArgb(0xB8,0x5C,0x00); } }
        public static Color Err       { get { return IsDark ? Color.FromArgb(0xE0,0x50,0x50) : Color.FromArgb(0xC4,0x00,0x00); } }
        public static Color SelBg     { get { return IsDark ? Color.FromArgb(0x3A,0x3A,0x5C) : Color.FromArgb(0xDD,0xE8,0xF5); } }
        public static Color LogBg     { get { return IsDark ? Color.FromArgb(0x12,0x12,0x1E) : Color.FromArgb(0xFA,0xFA,0xFA); } }
        public static Color LogFg     { get { return IsDark ? Color.FromArgb(0xAA,0xAA,0xCC) : Color.FromArgb(0x30,0x30,0x4A); } }
        public static Color EntryBg   { get { return IsDark ? Color.FromArgb(0x2E,0x2E,0x4E) : Color.White; } }
        public static Color EntryFg   { get { return IsDark ? Color.FromArgb(0xE8,0xE8,0xF0) : Color.FromArgb(0x1A,0x1A,0x2E); } }
        public static Color Danger    { get { return Color.FromArgb(0xC4, 0x00, 0x00); } }
    }

    // =========================================================================
    //  Control factory  (mirrors Triquetra.cs Ctrl class)
    // =========================================================================
    static class Ctrl
    {
        public static Font SegoUI(float size, FontStyle style)
        { return new Font("Segoe UI", size, style); }
        public static Font SegoUI(float size) { return SegoUI(size, FontStyle.Regular); }
        public static Font SegoUI()           { return SegoUI(9.75f); }

        public static Button AccentButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(9.75f, FontStyle.Bold);
            b.BackColor = Theme.Accent; b.ForeColor = Theme.AccentFg;
            b.Height    = 34; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize             = 0;
            b.FlatAppearance.MouseOverBackColor     = Theme.AccentHov;
            return b;
        }

        public static Button NormalButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(); b.BackColor = Theme.Surface; b.ForeColor = Theme.Fg;
            b.Height    = 30; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize         = 1;
            b.FlatAppearance.BorderColor        = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Theme.SelBg;
            return b;
        }

        public static Button DangerButton(string text)
        {
            Button b = new Button();
            b.Text      = text; b.FlatStyle = FlatStyle.Flat;
            b.Font      = SegoUI(); b.BackColor = Theme.Danger; b.ForeColor = Color.White;
            b.Height    = 30; b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize         = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xA0, 0x00, 0x00);
            return b;
        }

        public static Label MakeLabel(string text, bool dim, float size)
        {
            Label l = new Label();
            l.Text      = text; l.Font = SegoUI(size);
            l.ForeColor = dim ? Theme.FgDim : Theme.Fg;
            l.BackColor = Color.Transparent; l.AutoSize = true;
            return l;
        }
        public static Label MakeLabel(string text, bool dim) { return MakeLabel(text, dim, 9.75f); }
        public static Label MakeLabel(string text)           { return MakeLabel(text, false); }

        public static TextBox MakeEntry(bool password)
        {
            TextBox tb = new TextBox();
            tb.Font = SegoUI();
            if (Theme.IsDark)
            {
                tb.BackColor = Theme.EntryBg;
                tb.ForeColor = Theme.EntryFg;
            }
            else
            {
                tb.BackColor = SystemColors.Window;
                tb.ForeColor = SystemColors.WindowText;
            }
            // BorderStyle is owned by Bordered()/RestyleBordered() below, not
            // set here - every MakeEntry() caller wraps its result in
            // Bordered() before adding it to the form.
            if (password) tb.UseSystemPasswordChar = true;
            return tb;
        }
        public static TextBox MakeEntry() { return MakeEntry(false); }

        // BorderStyle.FixedSingle's border always renders a fixed light gray
        // (confirmed via live pixel-check: ~240,240,240) regardless of theme -
        // WinForms doesn't expose that border's color as a settable property.
        // Wraps tb in a Panel and draws the border as that panel's own
        // BackColor instead: dark mode sets tb.BorderStyle = None and gives
        // the wrapper a 1px Theme.Border-colored Padding; light mode keeps
        // tb's native BorderStyle.Fixed3D untouched and the wrapper is a
        // zero-padding, transparent pass-through, so light mode's rendering
        // (already verified working) doesn't change at all.
        //
        // Uses tb.PreferredHeight, not the caller's own tb.Height, to size
        // the wrapper - a single-line TextBox silently ignores any explicit
        // Height (WinForms clamps it to the font's natural line height
        // regardless of Dock/explicit size), so sizing the wrapper off the
        // caller's requested Height left a real gap of leftover Dock.Fill
        // space below the text box, which then showed through as an
        // oversized border-colored band along the wrapper's own bottom edge
        // (confirmed live: an 8px-tall band vs.the intended 1px elsewhere).
        public static Panel Bordered(TextBox tb)
        {
            Panel p = new Panel();
            tb.Dock = DockStyle.Fill;
            p.Controls.Add(tb);
            RestyleBordered(p);
            return p;
        }

        public static void RestyleBordered(Panel p)
        {
            TextBox tb = p.Controls.Count > 0 ? p.Controls[0] as TextBox : null;
            if (tb == null) return;
            if (Theme.IsDark)
            {
                tb.BorderStyle = BorderStyle.None;
                p.Padding      = new Padding(1);
                p.BackColor    = Theme.Border;
            }
            else
            {
                tb.BorderStyle = BorderStyle.Fixed3D;
                p.Padding      = new Padding(0);
                p.BackColor    = Color.Transparent;
            }
            p.Height = tb.PreferredHeight + p.Padding.Vertical;
        }

        public static CheckBox MakeCheck(string text)
        {
            CheckBox cb = new CheckBox();
            cb.Text      = text; cb.Font = SegoUI();
            cb.ForeColor = Theme.Fg; cb.BackColor = Color.Transparent; cb.AutoSize = true;
            return cb;
        }
    }

}
