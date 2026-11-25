// BWUI — design tokens, palettes and theme switching. See DESIGN.md §3.
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace Bwui
{
    public enum ThemeMode { System, Light, Dark }
    public enum WidgetSize { Compact, Regular, Wide }
    public enum TextStyle { Caption, Label, Body, Title, Value, Display }
    public enum Status { Normal, Caution, Critical }

    /// <summary>Layout and type tokens. Everything sits on a 4px grid.</summary>
    public static class Tokens
    {
        public const double Space1 = 4, Space2 = 8, Space3 = 12, Space4 = 16, Space5 = 20, Space6 = 24;
        public const double CardRadius = 24, CardPadding = 16, InnerRadius = CardRadius - CardPadding, ShadowRoom = 16;
        public const double IconSize = 20, ButtonSize = 28, GlyphSize = 14, StatusGlyphSize = 12;
        public const double MeterHeight = 8, RingSize = 64, RingStroke = 8, HeaderHeight = 24;
        public const double MenuRadius = 8;
        public const string FontFamily = "Segoe UI Variable Text, Segoe UI, Malgun Gothic";
        public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

        public static double FontSize(TextStyle s)
        {
            switch (s)
            {
                case TextStyle.Caption: return 11;
                case TextStyle.Label: return 12;
                case TextStyle.Body: return 13;
                case TextStyle.Title: return 14;
                case TextStyle.Value: return 15;
                default: return 22;
            }
        }

        public static bool IsBold(TextStyle s) { return s == TextStyle.Title || s == TextStyle.Value || s == TextStyle.Display; }

        public static double ContentWidth(WidgetSize s)
        {
            switch (s)
            {
                case WidgetSize.Compact: return 200;
                case WidgetSize.Wide: return 352;
                default: return 264;
            }
        }
    }

    /// <summary>Fluent icon-font code points used by BWUI.</summary>
    public static class Glyphs
    {
        public const string Warning = "", Error = "", Refresh = "", Check = "",
            More = "", Settings = "", HardDrive = "", Info = "", Mail = "",
            ChevronUp = "", ChevronDown = "", ChevronLeft = "", ChevronRight = "", Close = "", Download = "";
    }

    public sealed class Palette
    {
        public string Name;
        public bool IsDark, IsHighContrast;
        public Color Surface, SurfaceSubtle, SurfaceHover, Track, Divider, TextPrimary, TextSecondary, TextTertiary,
            Caution, Critical, Success, Shadow, Desk, Border, MenuBorder;
        public double ShadowOpacity;

        public static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        public static readonly Palette Light = new Palette
        {
            Name = "light",
            Surface = Hex("#FFFFFF"), SurfaceSubtle = Hex("#F4F3EF"), SurfaceHover = Hex("#E9E7E0"),
            Track = Hex("#F1EFE8"), Divider = Hex("#EFEDE7"),
            TextPrimary = Hex("#1F1E1C"), TextSecondary = Hex("#6B6962"), TextTertiary = Hex("#767470"),
            Caution = Hex("#9D5D00"), Critical = Hex("#C42B1C"), Success = Hex("#0F7B0F"),
            Shadow = Hex("#1E283C"), ShadowOpacity = 0.16, Desk = Hex("#C9D6E3"),
            Border = Colors.Transparent, MenuBorder = Hex("#E9E7E0")
        };

        public static readonly Palette Dark = new Palette
        {
            Name = "dark", IsDark = true,
            Surface = Hex("#2B2A28"), SurfaceSubtle = Hex("#383734"), SurfaceHover = Hex("#45433F"),
            Track = Hex("#3D3C38"), Divider = Hex("#3A3936"),
            TextPrimary = Hex("#F3F2EE"), TextSecondary = Hex("#BDBBB4"), TextTertiary = Hex("#A3A19A"),
            Caution = Hex("#FCE100"), Critical = Hex("#FF99A4"), Success = Hex("#6CCB5F"),
            Shadow = Hex("#000000"), ShadowOpacity = 0.45, Desk = Hex("#1B2430"),
            Border = Colors.Transparent, MenuBorder = Hex("#45433F")
        };

        /// <summary>Built from the live Windows system colors.</summary>
        public static Palette HighContrast()
        {
            var bg = SystemColors.WindowColor;
            var fg = SystemColors.WindowTextColor;
            var hi = SystemColors.HighlightColor;
            return new Palette
            {
                Name = "hc", IsHighContrast = true, IsDark = Luminance(bg) < 0.5,
                Surface = bg, SurfaceSubtle = bg, SurfaceHover = SystemColors.HighlightColor,
                Track = SystemColors.GrayTextColor, Divider = fg,
                TextPrimary = fg, TextSecondary = fg, TextTertiary = fg,
                Caution = hi, Critical = hi, Success = hi,
                Shadow = Colors.Black, ShadowOpacity = 0, Desk = SystemColors.DesktopColor,
                Border = fg, MenuBorder = fg
            };
        }

        /// <summary>The accent to use in this palette, from a light/dark pair.</summary>
        public Color Accent(Color light, Color dark)
        {
            if (IsHighContrast) return SystemColors.HighlightColor;
            return IsDark ? dark : light;
        }

        public Color StatusColor(Status s, Color accent)
        {
            if (s == Status.Critical) return Critical;
            if (s == Status.Caution) return Caution;
            return accent;
        }

        // ---- WCAG contrast ----

        static double Channel(byte c)
        {
            double v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        public static double Luminance(Color c) { return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B); }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>Text report of every token pair that has a contrast requirement (DESIGN.md C2).</summary>
        public static string ContrastReport(params KeyValuePair<string, Color[]>[] accents)
        {
            var sb = new StringBuilder();
            foreach (var p in new[] { Light, Dark })
            {
                sb.AppendLine("[" + p.Name + "]");
                Row(sb, "TextPrimary / Surface", p.TextPrimary, p.Surface, 4.5);
                Row(sb, "TextSecondary / Surface", p.TextSecondary, p.Surface, 4.5);
                Row(sb, "TextTertiary / Surface", p.TextTertiary, p.Surface, 4.5);
                Row(sb, "TextSecondary / SurfaceSubtle", p.TextSecondary, p.SurfaceSubtle, 4.5);
                Row(sb, "TextPrimary / SurfaceHover", p.TextPrimary, p.SurfaceHover, 4.5);
                Row(sb, "Caution / Surface", p.Caution, p.Surface, 4.5);
                Row(sb, "Critical / Surface", p.Critical, p.Surface, 4.5);
                Row(sb, "Success / Surface", p.Success, p.Surface, 4.5);
                foreach (var a in accents)
                    Row(sb, "Accent " + a.Key + " / Surface", p.IsDark ? a.Value[1] : a.Value[0], p.Surface, 3.0);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static void Row(StringBuilder sb, string name, Color fg, Color bg, double need)
        {
            double r = Contrast(fg, bg);
            sb.AppendLine(string.Format("{0,-36} {1,6:0.00}  need {2:0.0}  {3}", name, r, need, r >= need ? "ok" : "FAIL"));
        }
    }

    /// <summary>The current palette, and switching between System / Light / Dark / High Contrast.</summary>
    public static class Theme
    {
        static ThemeMode mode = ThemeMode.System;
        static Palette current = Palette.Light;
        static ResourceDictionary resources;
        static bool watching;

        public static event EventHandler Changed;
        public static Palette Current { get { return current; } }

        public static ThemeMode Mode
        {
            get { return mode; }
            set { mode = value; Refresh(); }
        }

        /// <summary>Brushes and menu styles, merged into each BWUI window. Updated in place on theme change.</summary>
        public static ResourceDictionary Resources
        {
            get
            {
                if (resources == null)
                {
                    resources = (ResourceDictionary)XamlReader.Parse(MenuXaml);
                    UpdateResources();
                }
                return resources;
            }
        }

        public static bool SystemUsesDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static void Refresh()
        {
            if (SystemParameters.HighContrast) current = Palette.HighContrast();
            else if (mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemUsesDark())) current = Palette.Dark;
            else current = Palette.Light;
            UpdateResources();
            var h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        }

        /// <summary>Follow the system theme and High Contrast while the app runs.</summary>
        public static void Watch()
        {
            if (watching) return;
            watching = true;
            var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.Accessibility)
                    d.BeginInvoke(new Action(Refresh));
            };
            SystemParameters.StaticPropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "HighContrast") d.BeginInvoke(new Action(Refresh));
            };
        }

        public static ThemeMode Parse(string s)
        {
            if (s == "light") return ThemeMode.Light;
            if (s == "dark") return ThemeMode.Dark;
            return ThemeMode.System;
        }

        public static string ToKey(ThemeMode m) { return m == ThemeMode.Light ? "light" : m == ThemeMode.Dark ? "dark" : "system"; }

        static void UpdateResources()
        {
            if (resources == null) return;
            var p = current;
            resources["Bwui.Surface"] = new SolidColorBrush(p.Surface);
            resources["Bwui.Hover"] = new SolidColorBrush(p.SurfaceHover);
            resources["Bwui.Text"] = new SolidColorBrush(p.TextPrimary);
            resources["Bwui.TextSecondary"] = new SolidColorBrush(p.TextSecondary);
            resources["Bwui.Divider"] = new SolidColorBrush(p.Divider);
            resources["Bwui.MenuBorder"] = new SolidColorBrush(p.MenuBorder);
        }

        // Context menu look (DESIGN.md §6.3): Surface, radius 8, Body text, a check column, no submenus.
        const string MenuXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ContextMenu'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='True'/>
    <Setter Property='FontFamily' Value='" + Tokens.FontFamily + @"'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='{DynamicResource Bwui.Surface}' BorderBrush='{DynamicResource Bwui.MenuBorder}'
                  BorderThickness='1' CornerRadius='8' Padding='4' MinWidth='180'>
            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Foreground' Value='{DynamicResource Bwui.Text}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Padding='8,6,16,6' SnapsToDevicePixels='True'>
            <DockPanel>
              <TextBlock x:Name='Chk' Text='&#xE73E;' FontFamily='" + Tokens.IconFont + @"' FontSize='12' Width='24'
                         VerticalAlignment='Center' Foreground='{DynamicResource Bwui.Text}' Visibility='Hidden'/>
              <ContentPresenter ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center'/>
            </DockPanel>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'><Setter TargetName='Chk' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource Bwui.Hover}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Margin='8,4' Background='{DynamicResource Bwui.Divider}'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
    }
}
