using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PenNoteBridge
{
    internal enum DesktopThemeMode { System, Light, Dark }

    internal static class DesktopTheme
    {
        internal static readonly Color Canvas = Color.FromArgb(25, 22, 27);
        internal static readonly Color Surface = Color.FromArgb(33, 29, 35);
        internal static readonly Color Paper = Color.FromArgb(40, 35, 42);
        internal static readonly Color Ink = Color.FromArgb(247, 237, 242);
        internal static readonly Color Muted = Color.FromArgb(192, 174, 184);
        internal static readonly Color Outline = Color.FromArgb(71, 57, 68);
        internal static readonly Color Accent = Color.FromArgb(244, 166, 192);
        internal static readonly Color AccentFill = Color.FromArgb(161, 62, 97);
        internal static readonly Color AccentText = Color.FromArgb(255, 247, 251);
        internal static readonly Color SoftAccent = Color.FromArgb(72, 41, 55);
        internal static readonly Color AccentLine = Color.FromArgb(116, 80, 104);
        internal static readonly Color Hover = Color.FromArgb(55, 45, 54);
        internal static readonly Color Pressed = Color.FromArgb(75, 55, 67);
        internal static readonly Color Green = Color.FromArgb(138, 217, 177);
        internal static readonly Color Error = Color.FromArgb(255, 159, 174);

        const int UseImmersiveDarkMode = 20;
        const int LegacyImmersiveDarkMode = 19;
        const int CaptionColor = 35;
        const int CaptionTextColor = 36;
        const int DefaultColor = -1;
        static string preferenceFile;
        static int lastSystemPoll;

        internal static DesktopThemeMode Mode { get; private set; }
        internal static bool IsDark { get; private set; }
        internal static event EventHandler Changed;

        internal static void Initialize(string profile)
        {
            preferenceFile = Path.Combine(profile, "appearance.txt");
            Mode = DesktopThemeMode.System;
            try
            {
                DesktopThemeMode saved;
                if (Enum.TryParse(File.ReadAllText(preferenceFile).Trim(), true, out saved)
                    && Enum.IsDefined(typeof(DesktopThemeMode), saved))
                    Mode = saved;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            IsDark = ResolveMode(Mode);
            lastSystemPoll = Environment.TickCount;
        }

        internal static void SetMode(DesktopThemeMode mode)
        {
            if (Mode == mode) return;
            File.WriteAllText(preferenceFile, mode.ToString());
            Mode = mode;
            IsDark = ResolveMode(mode);
            NotifyChanged();
        }

        internal static void RefreshSystemTheme()
        {
            if (Mode != DesktopThemeMode.System) return;
            int now = Environment.TickCount;
            if (unchecked(now - lastSystemPoll) < 1000) return;
            lastSystemPoll = now;
            bool dark = ReadWindowsAppearance();
            if (dark == IsDark) return;
            IsDark = dark;
            NotifyChanged();
        }

        static bool ResolveMode(DesktopThemeMode mode)
        {
            return mode == DesktopThemeMode.Dark
                || (mode == DesktopThemeMode.System && ReadWindowsAppearance());
        }

        static bool ReadWindowsAppearance()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && object.Equals(key.GetValue("AppsUseLightTheme"), 0);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
        }

        static void NotifyChanged()
        {
            var changed = Changed;
            if (changed != null) changed(null, EventArgs.Empty);
        }

        internal static void ApplyTitleBar(Form form)
        {
            if (!form.IsHandleCreated || form.FormBorderStyle == FormBorderStyle.None) return;
            IntPtr window = form.Handle;
            int dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(window, LegacyImmersiveDarkMode, ref dark, sizeof(int));
            int background = IsDark ? ColorTranslator.ToWin32(Surface) : DefaultColor;
            int text = IsDark ? ColorTranslator.ToWin32(Ink) : DefaultColor;
            DwmSetWindowAttribute(window, CaptionColor, ref background, sizeof(int));
            DwmSetWindowAttribute(window, CaptionTextColor, ref text, sizeof(int));
        }

        internal static void ApplyMenu(ContextMenuStrip menu)
        {
            if (IsDark)
                menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
            else
                menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            menu.BackColor = IsDark ? Paper : Color.Empty;
            menu.ForeColor = IsDark ? Ink : Color.Empty;
        }

        sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Paper; } }
            public override Color ImageMarginGradientBegin { get { return Paper; } }
            public override Color ImageMarginGradientMiddle { get { return Paper; } }
            public override Color ImageMarginGradientEnd { get { return Paper; } }
            public override Color MenuBorder { get { return Outline; } }
            public override Color MenuItemBorder { get { return AccentLine; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color SeparatorDark { get { return Outline; } }
            public override Color SeparatorLight { get { return Outline; } }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
