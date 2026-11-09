using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace WenCun.Tests;

public sealed class VisualTokenTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly string[] ControlSurfaces = ["SurfaceBrush", "CanvasBrush", "SubtleBrush", "RowHoverBrush", "AccentSoftBrush", "NavActiveBrush", "NavHoverBrush"];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void SmallFaintTextIsReadableOnPaperCanvasAndInteractiveSurfaces(string theme)
    {
        var brushes = Brushes(theme);
        foreach (var surface in ControlSurfaces)
            AssertContrast(brushes["FaintBrush"], brushes[surface], 4.5, $"{theme}: Faint/{surface}");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void InteractiveOutlinesAndFocusRemainVisibleOnAllControlSurfaces(string theme)
    {
        var brushes = Brushes(theme);
        Assert.True(brushes.ContainsKey("ControlBorderBrush"), "Interactive outlines need a token separate from card borders.");
        Assert.NotEqual(brushes["BorderBrush"], brushes["ControlBorderBrush"]);
        foreach (var surface in ControlSurfaces)
        {
            AssertContrast(brushes["ControlBorderBrush"], brushes[surface], 3, $"{theme}: control/{surface}");
            AssertContrast(brushes["AccentBrush"], brushes[surface], 3, $"{theme}: focus/{surface}");
        }
        AssertContrast(brushes["OnPrimaryBrush"], brushes["PrimaryBrush"], 3, $"{theme}: selection mark");
        Assert.Equal("{DynamicResource BorderBrush}", Setter(Style("Card"), "BorderBrush"));
    }

    [Theory]
    [InlineData("CheckBox", "Box")]
    [InlineData("RadioButton", "Ring")]
    public void ChoiceTargetsFitInlineRowsAndKeepTheSixteenPixelGlyph(string control, string glyphName)
    {
        var style = Style(control);
        foreach (var dimension in new[] { "MinWidth", "MinHeight" })
        {
            Assert.True(double.TryParse(Setter(style, dimension), CultureInfo.InvariantCulture, out var size));
            Assert.InRange(size, 24, 26);
        }
        var template = Template(style);
        var glyph = Named(template, glyphName);
        Assert.Equal("16", (string?)glyph.Attribute("Width"));
        Assert.Equal("16", (string?)glyph.Attribute("Height"));
        Assert.Equal("{DynamicResource ControlBorderBrush}", (string?)glyph.Attribute("BorderBrush"));
        var target = glyph.Parent!;
        Assert.Equal("24", (string?)target.Attribute("Width"));
        Assert.Equal("24", (string?)target.Attribute("Height"));
        Assert.Equal("Transparent", (string?)target.Attribute("Background"));
        Assert.Equal("26", Setter(Style("RowAction"), "MinHeight"));
    }

    [Theory]
    [InlineData("False", "Collapsed", "Collapsed")]
    [InlineData("True", "Visible", "Collapsed")]
    [InlineData("{x:Null}", "Collapsed", "Visible")]
    public void CheckboxDistinguishesUncheckedCheckedAndMixed(string state, string check, string mixed)
    {
        var template = Template(Style("CheckBox"));
        Assert.Equal(check, StateValue(template, "Check", "Visibility", ("IsChecked", state)));
        Assert.Equal(mixed, StateValue(template, "MixedMark", "Visibility", ("IsChecked", state)));
        if (state != "False")
            Assert.Equal("{DynamicResource PrimaryBrush}", StateValue(template, "Box", "Background", ("IsChecked", state)));
    }

    [Theory]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    public void ChoiceFocusIsSeparateFromCheckedAndHoverBorders(string control)
    {
        var template = Template(Style(control));
        foreach (var state in new[] { "False", "True", "{x:Null}" })
        {
            Assert.Equal("Collapsed", StateValue(template, "FocusRing", "Visibility", ("IsChecked", state)));
            Assert.Equal("Visible", StateValue(template, "FocusRing", "Visibility",
                ("IsChecked", state), ("IsKeyboardFocused", "True"), ("IsMouseOver", "True")));
        }
        var ring = Named(template, "FocusRing");
        Assert.Equal("{DynamicResource AccentBrush}", (string?)ring.Attribute("BorderBrush"));
        Assert.Equal("1", (string?)ring.Attribute("BorderThickness"));
    }

    [Fact]
    public void FormatSelectionHasANonColorCueWithoutResizingTheChip()
    {
        var template = Template(Style("FormatChip"));
        Assert.Equal("Collapsed", StateValue(template, "SelectionMark", "Visibility", ("IsChecked", "False")));
        Assert.Equal("Visible", StateValue(template, "SelectionMark", "Visibility", ("IsChecked", "True")));
        var mark = Named(template, "SelectionMark");
        Assert.Equal("Bottom", (string?)mark.Attribute("VerticalAlignment"));
        Assert.Equal("2", (string?)mark.Attribute("Height"));
        Assert.DoesNotContain(Triggers(template).Where(trigger => (string?)trigger.Attribute("Property") == "IsChecked")
            .Elements(Wpf + "Setter"), setter => (string?)setter.Attribute("Property") is "Width" or "Height" or "Padding" or "Margin");
    }

    [Fact]
    public void TextBoxDoesNotApplyItsNativeScrollPaddingTwice()
    {
        var style = Style("TextBox");
        Assert.Equal("13", Setter(style, "FontSize"));
        Assert.Equal("Center", Setter(style, "VerticalContentAlignment"));
        var template = Template(style);
        var host = Named(template, "PART_ContentHost");
        Assert.Equal(Wpf + "ScrollViewer", host.Name);
        // TextBoxBase already forwards Padding to the native scroll host. Another inset clips 34px inputs.
        foreach (var element in host.AncestorsAndSelf().TakeWhile(element => element != template))
        {
            Assert.Null(element.Attribute("Margin"));
            Assert.Null(element.Attribute("Padding"));
        }
    }

    [Theory]
    [InlineData("BaseButton")]
    [InlineData("PrimaryButton")]
    [InlineData("GhostButton")]
    [InlineData("NavButton")]
    [InlineData("FilterTab")]
    [InlineData("ListChoice")]
    [InlineData("FormatChip")]
    [InlineData("TextBox")]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    [InlineData("ListBoxItem")]
    public void KeyboardControlsProvideFocusAndDisabledFeedback(string key)
    {
        var style = Style(key);
        var template = Template(style);
        var focus = Setter(style, "FocusVisualStyle");
        Assert.True(focus == "{StaticResource FocusVisual}" || Triggers(template).Any(trigger =>
            (string?)trigger.Attribute("Property") is "IsKeyboardFocused" or "IsKeyboardFocusWithin"));
        var disabled = Assert.Single(Triggers(template), trigger =>
            (string?)trigger.Attribute("Property") == "IsEnabled" && (string?)trigger.Attribute("Value") == "False");
        var opacity = Assert.Single(disabled.Elements(Wpf + "Setter"), setter => (string?)setter.Attribute("Property") == "Opacity");
        Assert.InRange(double.Parse((string)opacity.Attribute("Value")!, CultureInfo.InvariantCulture), 0.3, 0.6);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void BrandUsesContrastingBoxAndPaperWithTheOriginalGeometry(string theme)
    {
        var paths = Read("Assets/wencun-mark.xaml").Descendants(Wpf + "Path").ToArray();
        XNamespace svg = "http://www.w3.org/2000/svg";
        var original = Read("Assets/wencun-mark.svg").Descendants(svg + "path").ToArray();
        Assert.Equal(original.Select(path => (string?)path.Attribute("d")), paths.Select(path => (string?)path.Attribute("Data")));
        Assert.Equal("{DynamicResource LogoGroundBrush}", (string?)paths[0].Attribute("Fill"));
        Assert.Equal("{DynamicResource LogoStrokeBrush}", (string?)paths[1].Attribute("Fill"));
        Assert.Equal("{DynamicResource LogoGroundBrush}", (string?)paths[2].Attribute("Stroke"));
        Assert.Equal("{DynamicResource LogoDotBrush}", (string?)paths[3].Attribute("Fill"));
        Assert.Equal("{DynamicResource LogoGroundBrush}", (string?)paths[4].Attribute("Fill"));
        Assert.Equal("{DynamicResource LogoStrokeBrush}", (string?)paths[5].Attribute("Stroke"));
        var brushes = Brushes(theme);
        AssertContrast(brushes["LogoGroundBrush"], brushes["SidebarBrush"], 3, $"{theme}: box/sidebar");
        AssertContrast(brushes["LogoGroundBrush"], brushes["LogoStrokeBrush"], 4.5, $"{theme}: box/paper");
    }

    private static XDocument Read(string relativePath, [CallerFilePath] string source = "") =>
        XDocument.Load(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "WenCun.Desktop", relativePath));

    private static Dictionary<string, string> Brushes(string theme) => Read($"Themes/{theme}.xaml")
        .Descendants(Wpf + "SolidColorBrush").ToDictionary(brush => (string)brush.Attribute(Xaml + "Key")!, brush => (string)brush.Attribute("Color")!);

    private static XElement Style(string key) => Read("Themes/Controls.xaml").Root!.Elements(Wpf + "Style")
        .Single(style => (string?)style.Attribute(Xaml + "Key") == key ||
            (style.Attribute(Xaml + "Key") is null && (string?)style.Attribute("TargetType") == key));

    private static string? Setter(XElement style, string property) => (string?)style.Elements(Wpf + "Setter")
        .SingleOrDefault(setter => (string?)setter.Attribute("Property") == property)?.Attribute("Value");

    private static XElement Template(XElement style) => style.Descendants(Wpf + "ControlTemplate").Single();
    private static XElement Named(XElement template, string name) => Assert.Single(template.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);
    private static IEnumerable<XElement> Triggers(XElement template) => template.Element(Wpf + "ControlTemplate.Triggers")!.Elements(Wpf + "Trigger");

    // Evaluate the simple property triggers used by these templates without creating a second WPF Application.
    private static string? StateValue(XElement template, string target, string property, params (string Property, string Value)[] state)
    {
        var value = (string?)Named(template, target).Attribute(property);
        foreach (var trigger in Triggers(template).Where(trigger => state.Contains(((string)trigger.Attribute("Property")!, (string)trigger.Attribute("Value")!))))
            foreach (var setter in trigger.Elements(Wpf + "Setter").Where(setter =>
                (string?)setter.Attribute("TargetName") == target && (string?)setter.Attribute("Property") == property))
                value = (string?)setter.Attribute("Value");
        return value;
    }

    private static void AssertContrast(string foreground, string background, double minimum, string context)
    {
        var first = Luminance(foreground);
        var second = Luminance(background);
        var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(contrast >= minimum, $"{context}: {foreground} on {background} is {contrast:F2}:1; requires {minimum}:1.");
    }

    private static double Luminance(string color)
    {
        var rgb = new[] { 1, 3, 5 }.Select(index => int.Parse(color.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0)
            .Select(value => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4)).ToArray();
        return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
    }
}
