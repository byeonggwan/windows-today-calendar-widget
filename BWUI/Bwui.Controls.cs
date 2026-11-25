// BWUI — component factories. See DESIGN.md §4.
using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Bwui
{
    public static class Ui
    {
        static Palette P { get { return Theme.Current; } }

        public static Brush Br(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color DefaultColor(TextStyle s)
        {
            if (s == TextStyle.Caption) return P.TextTertiary;
            if (s == TextStyle.Label) return P.TextSecondary;
            return P.TextPrimary;
        }

        // ---------- text ----------

        public static TextBlock Text(string text, TextStyle style)
        {
            return Text(text, style, DefaultColor(style));
        }

        public static TextBlock Text(string text, TextStyle style, Color color)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = Tokens.FontSize(style),
                FontWeight = Tokens.IsBold(style) ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Br(color),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        public static Run Span(string text, TextStyle style, Color color)
        {
            return new Run(text)
            {
                FontSize = Tokens.FontSize(style),
                FontWeight = Tokens.IsBold(style) ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Br(color)
            };
        }

        public static TextBlock Glyph(string glyph, double size, Color color)
        {
            var t = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(Tokens.IconFont),
                FontSize = size,
                Foreground = Br(color),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            return t;
        }

        // ---------- status ----------

        /// <summary>Status of a "remaining %" value. Defaults: caution ≤ 20, critical ≤ 5 (DESIGN.md §3.5).</summary>
        public static Status StatusForRemaining(double pct, double caution = 20, double critical = 5)
        {
            if (pct < 0) return Status.Normal;
            if (pct <= critical) return Status.Critical;
            if (pct <= caution) return Status.Caution;
            return Status.Normal;
        }

        /// <summary>A value with a status glyph when it is not normal, so color is never the only signal (C3).</summary>
        public static FrameworkElement StatusValue(string text, Status status, TextStyle style = TextStyle.Value)
        {
            if (status == Status.Normal) return Text(text, style);
            var c = P.StatusColor(status, P.TextPrimary);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = Glyph(status == Status.Critical ? Glyphs.Error : Glyphs.Warning, Tokens.StatusGlyphSize, c);
            g.Margin = new Thickness(0, 1, Tokens.Space1, 0);
            sp.Children.Add(g);
            sp.Children.Add(Text(text, style, c));
            AutomationProperties.SetName(sp, text + (status == Status.Critical ? " (" + Bwui.Text.Critical + ")" : " (" + Bwui.Text.Caution + ")"));
            return sp;
        }

        // ---------- icons ----------

        /// <summary>20px header icon: the service logo, or an accent square with a glyph.</summary>
        public static FrameworkElement Icon(ImageSource logo, Color accent, string fallbackGlyph = null)
        {
            if (logo != null)
            {
                var im = new Image { Source = logo, Width = Tokens.IconSize, Height = Tokens.IconSize, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                return im;
            }
            var b = new Border
            {
                Width = Tokens.IconSize, Height = Tokens.IconSize,
                CornerRadius = new CornerRadius(6),
                Background = Br(accent),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (fallbackGlyph != null)
                b.Child = Glyph(fallbackGlyph, 12, P.IsHighContrast ? SystemColors.HighlightTextColor
                    : Palette.Contrast(Colors.White, accent) >= 3 ? Colors.White : P.Surface);
            return b;
        }

        public static ImageSource LoadPng(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // ---------- layout ----------

        /// <summary>Header: icon, title, and an optional trailing element (caption or icon button).</summary>
        public static FrameworkElement Header(FrameworkElement icon, string title, UIElement trailing = null)
        {
            var d = new DockPanel { LastChildFill = true, MinHeight = Tokens.HeaderHeight };
            if (icon != null)
            {
                DockPanel.SetDock(icon, Dock.Left);
                d.Children.Add(icon);
            }
            if (trailing != null)
            {
                var fe = trailing as FrameworkElement;
                if (fe != null)
                {
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    fe.Margin = new Thickness(Tokens.Space2, 0, 0, 0);
                }
                DockPanel.SetDock(trailing, Dock.Right);
                d.Children.Add(trailing);
            }
            var t = Text(title, TextStyle.Title);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(icon != null ? Tokens.Space2 : 0, 0, 0, 0);
            d.Children.Add(t);
            return d;
        }

        public static Border Divider()
        {
            return new Border { Height = 1, Background = Br(P.Divider), Margin = new Thickness(0, Tokens.Space3, 0, Tokens.Space3) };
        }

        // ---------- buttons ----------

        static void Hover(Border b)
        {
            b.MouseEnter += (s, e) => b.Background = Br(P.SurfaceHover);
            b.MouseLeave += (s, e) => b.Background = Br(P.SurfaceSubtle);
        }

        static void Click(Border b, Action onClick)
        {
            // Handled here so the card's drag-to-move doesn't start from a button.
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (onClick != null) onClick(); };
        }

        /// <summary>28px round button. The tooltip doubles as the accessible name (C18).</summary>
        public static Border IconButton(string glyph, string tooltip, Action onClick)
        {
            return IconButton(Glyph(glyph, Tokens.GlyphSize, P.TextSecondary), tooltip, onClick);
        }

        public static Border IconButton(UIElement content, string tooltip, Action onClick)
        {
            var b = new Border
            {
                Width = Tokens.ButtonSize, Height = Tokens.ButtonSize,
                CornerRadius = new CornerRadius(Tokens.ButtonSize / 2),
                Background = Br(P.SurfaceSubtle),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = content
            };
            if (P.IsHighContrast) { b.BorderBrush = Br(P.Border); b.BorderThickness = new Thickness(1); }
            AutomationProperties.SetName(b, tooltip);
            Hover(b);
            Click(b, onClick);
            return b;
        }

        /// <summary>Pill text button, height 28 (used inside messages).</summary>
        public static Border TextButton(string text, Action onClick)
        {
            var t = Text(text, TextStyle.Label, P.TextPrimary);
            t.VerticalAlignment = VerticalAlignment.Center;
            var b = new Border
            {
                Height = Tokens.ButtonSize,
                Padding = new Thickness(Tokens.Space3, 0, Tokens.Space3, 0),
                CornerRadius = new CornerRadius(Tokens.ButtonSize / 2),
                Background = Br(P.SurfaceSubtle),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = t
            };
            if (P.IsHighContrast) { b.BorderBrush = Br(P.Border); b.BorderThickness = new Thickness(1); }
            AutomationProperties.SetName(b, text);
            Hover(b);
            Click(b, onClick);
            return b;
        }

        /// <summary>The card look (Surface, radius, padding, shadow room, the one shadow, HC border). Re-apply on Theme.Changed.</summary>
        public static void StyleCard(Border card)
        {
            var p = P;
            card.CornerRadius = new CornerRadius(Tokens.CardRadius);
            card.Padding = new Thickness(Tokens.CardPadding);
            card.Margin = new Thickness(Tokens.ShadowRoom);
            card.Background = Br(p.Surface);
            card.BorderBrush = Br(p.Border);
            card.BorderThickness = new Thickness(p.IsHighContrast ? 1 : 0);
            card.Effect = p.ShadowOpacity <= 0 ? null : new DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = p.ShadowOpacity, Color = p.Shadow
            };
        }

        /// <summary>TextButton with a disabled state: 45% opacity and not clickable.</summary>
        public static Border TextButton(string text, bool enabled, Action onClick)
        {
            var b = TextButton(text, onClick);
            if (!enabled) { b.IsEnabled = false; b.Opacity = 0.45; b.Cursor = null; }
            return b;
        }

        /// <summary>Clickable list row: transparent, SurfaceSubtle on hover, InnerRadius corners.</summary>
        public static Border ListRow(UIElement content, double height, string accessibleName, Action onClick)
        {
            var row = new Border
            {
                Height = height,
                Padding = new Thickness(Tokens.Space2, 0, Tokens.Space2, 0),
                CornerRadius = new CornerRadius(Tokens.InnerRadius),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = content
            };
            AutomationProperties.SetName(row, accessibleName);
            row.MouseEnter += (s, e) => row.Background = Br(P.SurfaceSubtle);
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (onClick != null) onClick(); };
            return row;
        }

        /// <summary>Count badge (20px pill) for a tile's top-right corner. The number is the cue, not the color (C3).</summary>
        public static Border CountBadge(int count, Color accent)
        {
            var p = P;
            var fg = p.IsHighContrast ? SystemColors.HighlightTextColor : p.Surface;
            var t = Text(count > 99 ? "99+" : count.ToString(), TextStyle.Caption, fg);
            t.FontWeight = FontWeights.SemiBold;
            t.TextTrimming = TextTrimming.None;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            return new Border
            {
                MinWidth = 20, Height = 20,
                Padding = new Thickness(Tokens.Space1, 0, Tokens.Space1, 0),
                CornerRadius = new CornerRadius(10),
                Background = Br(accent),
                BorderBrush = Br(p.Surface),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = t
            };
        }

        /// <summary>Status mark for a tile's bottom-right corner: a Surface disc with the status glyph (C3).</summary>
        public static Border StatusMark(Status status)
        {
            var p = P;
            var c = p.StatusColor(status, p.TextSecondary);
            double d = Tokens.Space5;
            return new Border
            {
                Width = d, Height = d,
                CornerRadius = new CornerRadius(d / 2),
                Background = Br(p.Surface),
                BorderBrush = Br(p.Border),
                BorderThickness = new Thickness(p.IsHighContrast ? 1 : 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = Glyph(status == Status.Critical ? Glyphs.Error : Glyphs.Warning, Tokens.StatusGlyphSize, c)
            };
        }

        // ---------- data ----------

        /// <summary>8px pill meter. A non-zero fill is at least as wide as it is tall.</summary>
        public static FrameworkElement Meter(double fraction, Color fill, double width)
        {
            var g = new Grid { Height = Tokens.MeterHeight, Width = width };
            var r = new CornerRadius(Tokens.MeterHeight / 2);
            g.Children.Add(new Border { Background = Br(P.Track), CornerRadius = r });
            if (fraction > 0)
                g.Children.Add(new Border
                {
                    Background = Br(fill),
                    CornerRadius = r,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(Tokens.MeterHeight, width * Math.Min(1, fraction))
                });
            return g;
        }

        /// <summary>Label + value on the left, caption on the right, meter below.</summary>
        public static FrameworkElement MeterRow(string label, string value, double fraction, string caption, Color accent, Status status, double width)
        {
            var sp = new StackPanel();
            var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, Tokens.Space2) };
            if (!string.IsNullOrEmpty(caption))
            {
                var c = Text(caption, TextStyle.Caption);
                c.VerticalAlignment = VerticalAlignment.Bottom;
                c.Margin = new Thickness(Tokens.Space2, 0, 0, 1);
                DockPanel.SetDock(c, Dock.Right);
                top.Children.Add(c);
            }
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var l = Text(label, TextStyle.Label);
            l.VerticalAlignment = VerticalAlignment.Bottom;
            l.Margin = new Thickness(0, 0, Tokens.Space2, 1);
            left.Children.Add(l);
            var v = StatusValue(value, status);
            v.VerticalAlignment = VerticalAlignment.Bottom;
            left.Children.Add(v);
            top.Children.Add(left);
            sp.Children.Add(top);
            sp.Children.Add(Meter(fraction, P.StatusColor(status, accent), width));
            AutomationProperties.SetName(sp, label + ", " + value + (string.IsNullOrEmpty(caption) ? "" : ", " + caption));
            return sp;
        }

        /// <summary>64px ring with the value centered and label/caption below.</summary>
        public static FrameworkElement Ring(double fraction, string value, string label, string caption, Color accent, Status status)
        {
            var col = P.StatusColor(status, accent);
            double s = Tokens.RingSize, sw = Tokens.RingStroke, rad = (s - sw) / 2, cx = s / 2, cy = s / 2;
            var g = new Grid { Width = s, Height = s, HorizontalAlignment = HorizontalAlignment.Center };
            g.Children.Add(new Ellipse { Stroke = Br(P.Track), StrokeThickness = sw });
            if (fraction >= 1)
                g.Children.Add(new Ellipse { Stroke = Br(col), StrokeThickness = sw });
            else if (fraction > 0)
            {
                double a = 2 * Math.PI * fraction;
                var fig = new PathFigure { StartPoint = new Point(cx, cy - rad), IsClosed = false };
                fig.Segments.Add(new ArcSegment(new Point(cx + rad * Math.Sin(a), cy - rad * Math.Cos(a)), new Size(rad, rad), 0, fraction > 0.5, SweepDirection.Clockwise, true));
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                g.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = Br(col), StrokeThickness = sw, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
            var v = StatusValue(value, status);
            v.HorizontalAlignment = HorizontalAlignment.Center;
            v.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(v);

            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(g);
            var lb = Text(label, TextStyle.Label);
            lb.HorizontalAlignment = HorizontalAlignment.Center;
            lb.Margin = new Thickness(0, Tokens.Space2, 0, 0);
            sp.Children.Add(lb);
            if (!string.IsNullOrEmpty(caption))
            {
                var c = Text(caption, TextStyle.Caption);
                c.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(c);
            }
            AutomationProperties.SetName(sp, label + ", " + value + (string.IsNullOrEmpty(caption) ? "" : ", " + caption));
            return sp;
        }

        public static UniformGrid RingRow(params UIElement[] rings)
        {
            var u = new UniformGrid { Columns = Math.Max(2, rings.Length), Rows = 1 };
            foreach (var r in rings) u.Children.Add(r);
            return u;
        }

        // ---------- states (DESIGN.md §5) ----------

        /// <summary>Caption message with an optional action, for empty / error / setup states.</summary>
        public static FrameworkElement Message(string text, string actionLabel = null, Action action = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, Tokens.Space2, 0, 0) };
            var t = Text(text, TextStyle.Caption);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            sp.Children.Add(t);
            if (actionLabel != null && action != null)
            {
                var b = TextButton(actionLabel, action);
                b.Margin = new Thickness(0, Tokens.Space2, 0, 0);
                sp.Children.Add(b);
            }
            return sp;
        }

        /// <summary>Loading placeholder: rounded bars standing in for content.</summary>
        public static FrameworkElement Skeleton(double width, int rows)
        {
            var sp = new StackPanel { Margin = new Thickness(0, Tokens.Space3, 0, 0) };
            AutomationProperties.SetName(sp, Bwui.Text.Loading);
            for (int i = 0; i < rows; i++)
            {
                var row = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : Tokens.Space3, 0, 0) };
                row.Children.Add(new Border { Width = width * 0.4, Height = Tokens.Space3, CornerRadius = new CornerRadius(4), Background = Br(P.SurfaceSubtle), HorizontalAlignment = HorizontalAlignment.Left });
                row.Children.Add(new Border { Width = width, Height = Tokens.MeterHeight, CornerRadius = new CornerRadius(4), Background = Br(P.SurfaceSubtle), Margin = new Thickness(0, Tokens.Space2, 0, 0) });
                sp.Children.Add(row);
            }
            return sp;
        }
    }

    /// <summary>How old a data source is, against the widget's thresholds (DESIGN.md §5).</summary>
    public struct Freshness
    {
        public TimeSpan Age;
        public bool HasData, IsStale, IsExpired;

        public static Freshness Of(DateTime? atUtc, TimeSpan staleAfter, TimeSpan expireAfter)
        {
            var f = new Freshness();
            if (!atUtc.HasValue) return f;
            f.HasData = true;
            f.Age = DateTime.UtcNow - atUtc.Value;
            if (f.Age < TimeSpan.Zero) f.Age = TimeSpan.Zero;
            f.IsStale = f.Age >= staleAfter;
            f.IsExpired = f.Age >= expireAfter;
            return f;
        }

        /// <summary>"as of 12 min ago" when stale, otherwise null.</summary>
        public string Caption { get { return IsStale ? Text.AsOf(Age) : null; } }

        /// <summary>Opacity for the section body: 50% once expired.</summary>
        public double BodyOpacity { get { return IsExpired ? 0.5 : 1; } }
    }
}
