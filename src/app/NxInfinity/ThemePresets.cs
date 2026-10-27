using System;
using System.Linq;
using Avalonia.Media;

namespace NxInfinity;

public static class ThemePresets
{
    public static readonly ThemePreset[] All = new ThemePreset[30]
    {
        new ThemePreset("dune", "Dune", Night: false, "#EBEBEB", "#F7F7F7", "#181818", "#666666", "#D3D3D3", "#DFDFDF", "#181818"),
        new ThemePreset("night", "Dune Night", Night: true, "#141414", "#212121", "#F4F4F4", "#ADADAD", "#383838", "#343434", "#F4F4F4"),
        new ThemePreset("home", "Original HOME", Night: false, "#EDF2F3", "#FFFFFF", "#232935", "#5E6974", "#D2DADD", "#E5EAED", "#4D52D9"),
        new ThemePreset("paper", "Paper & Ink", Night: false, "#F3F0E8", "#FFFCF5", "#252821", "#626659", "#D7D4C9", "#E5E1D6", "#314738"),
        new ThemePreset("sand", "Sandstone", Night: false, "#EDE4D5", "#F8F2E8", "#3B3028", "#705B4C", "#D0C2AC", "#DFD1BD", "#835329"),
        new ThemePreset("rose", "Cherry Blossom", Night: false, "#F3E9EC", "#FFF7F9", "#392A32", "#765765", "#DFC6D0", "#EAD5DE", "#963E62"),
        new ThemePreset("mint", "Mint Cartridge", Night: false, "#E4F0E9", "#F6FFF9", "#203C30", "#526D60", "#BFD6C8", "#CFE4D8", "#246C4F"),
        new ThemePreset("coast", "Coast", Night: false, "#E5EFF2", "#F7FCFF", "#213B47", "#526D79", "#C1D5DD", "#CDDFE7", "#21677B"),
        new ThemePreset("lilac", "Lilac", Night: false, "#ECE8F3", "#FAF7FF", "#362B48", "#695C7E", "#CEC3E0", "#DBD2E8", "#6D4A9B"),
        new ThemePreset("peach", "Peach Soda", Night: false, "#F4E7DD", "#FFF7F0", "#422E25", "#785B4D", "#DFC8B8", "#EBD5C4", "#9A4C2C"),
        new ThemePreset("arcade", "Arcade Paper", Night: false, "#E7E6DF", "#F8F7F0", "#282D38", "#626774", "#C9C9C2", "#D9D8D1", "#A43647"),
        new ThemePreset("pocket", "Pocket Green", Night: false, "#D5DFB0", "#E6EDCA", "#243821", "#4D6241", "#ACBD82", "#C1D096", "#354D2D"),
        new ThemePreset("graphite", "Graphite", Night: true, "#1A1E24", "#252B33", "#EDF1F6", "#AFBBC9", "#3F4956", "#343D49", "#A9C7E8"),
        new ThemePreset("midnight", "Midnight Blue", Night: true, "#111C2A", "#1D2B3D", "#E7F0FC", "#A5BCD5", "#354A64", "#2A3C52", "#92C9F0"),
        new ThemePreset("forest", "Forest After Dark", Night: true, "#15221B", "#20352A", "#EBF5EC", "#AAC4AF", "#395742", "#2B4535", "#A3D99E"),
        new ThemePreset("plum", "Plum Velvet", Night: true, "#281C2D", "#392940", "#F6EAF9", "#C5ABCE", "#5C4265", "#4A3552", "#DFACEE"),
        new ThemePreset("ember", "Ember", Night: true, "#281D19", "#3B2A22", "#FFF0E4", "#CCB4A2", "#5E4435", "#4B352A", "#F3B576"),
        new ThemePreset("sea", "Deep Sea", Night: true, "#10282C", "#1C3A40", "#E6F8F8", "#9EC4C8", "#355B62", "#284A51", "#7BD8D1"),
        new ThemePreset("contrast", "High Contrast", Night: true, "#000000", "#111111", "#FFFFFF", "#D1D1D1", "#8A8A8A", "#292929", "#FFE46B"),
        new ThemePreset("strawberry", "Strawberry Ribbon", Night: false, "#FCE6EF", "#FFF5F9", "#4B2035", "#805268", "#DDB4C7", "#F2CFDF", "#A32960"),
        new ThemePreset("cloud", "Cloud Parade", Night: false, "#E4F2FF", "#F8FCFF", "#233F5A", "#536F88", "#B9D3E8", "#D1E6F7", "#245D98"),
        new ThemePreset("pudding", "Pudding Cafe", Night: false, "#F5EBCD", "#FFF9E7", "#493B24", "#78633F", "#D6C49A", "#E9DBB6", "#84561C"),
        new ThemePreset("orchid", "Midnight Orchid", Night: true, "#21182E", "#30223F", "#F6ECFF", "#C9B4DC", "#624879", "#49335E", "#DDB4FF"),
        new ThemePreset("aurora", "Aurora", Night: true, "#101F29", "#192F3C", "#E6F8F4", "#A3C8CB", "#3A606C", "#284653", "#79E1C0"),
        new ThemePreset("sakura-night", "Sakura Night", Night: true, "#281A23", "#3C2734", "#FFF0F7", "#D3B1C4", "#654356", "#4D3442", "#FFA7CD"),
        new ThemePreset("lavender-cream", "Lavender Cream", Night: false, "#EFE9FA", "#FCF8FF", "#3C2A55", "#715C8A", "#CFC1E3", "#E0D4EF", "#6A4093"),
        new ThemePreset("citrus", "Citrus Grove", Night: false, "#EEF1D9", "#FAFCEB", "#314426", "#5F714E", "#C9D2AC", "#DFE6C5", "#547527"),
        new ThemePreset("obsidian", "Obsidian Red", Night: true, "#121216", "#202026", "#FAF1F3", "#BEB2B8", "#48404A", "#343038", "#FF8FA3"),
        new ThemePreset("violet-arcade", "Violet Arcade", Night: true, "#191529", "#28213D", "#F2EDFF", "#BCB0D9", "#514467", "#3B3152", "#B9A0FF"),
        new ThemePreset("snow", "Snow", Night: false, "#F4F6F8", "#FFFFFF", "#20272F", "#566470", "#CAD3DA", "#E2E7EC", "#325C87")
    };

    public static readonly string[] Patterns = new string[4] { "Plain", "Dots", "Grid", "Orbits" };

    public static ThemePreset Resolve(ThemeChoice choice)
    {
        ThemePreset themePreset = All.FirstOrDefault((ThemePreset p) => p.Id == choice.Preset) ?? All[0];
        if (choice.Accent != null)
        {
            return themePreset with
            {
                Accent = choice.Accent
            };
        }
        return themePreset;
    }

    public static double Contrast(string a, string b)
    {
        double val = L(a);
        double val2 = L(b);
        return (Math.Max(val, val2) + 0.05) / (Math.Min(val, val2) + 0.05);
        static double F(byte x)
        {
            double num = (double)(int)x / 255.0;
            if (!(num <= 0.04045))
            {
                return Math.Pow((num + 0.055) / 1.055, 2.4);
            }
            return num / 12.92;
        }
        static double L(string value)
        {
            Color color = Color.Parse(value);
            return 0.2126 * F(color.R) + 0.7152 * F(color.G) + 0.0722 * F(color.B);
        }
    }

    public static string FocusColor(ThemePreset p)
    {
        if (!(Contrast(p.Accent, p.Canvas) >= 3.0))
        {
            return p.Ink;
        }
        return p.Accent;
    }

    public static string AccentText(ThemePreset p)
    {
        if (!(Contrast(p.Accent, "#FFFFFF") >= Contrast(p.Accent, "#181818")))
        {
            return "#181818";
        }
        return "#FFFFFF";
    }

    public static bool ValidChoice(ThemeChoice? c)
    {
        if ((object)c != null && All.Any((ThemePreset p) => p.Id == c.Preset) && Patterns.Contains(c.Pattern))
        {
            if (c.Accent != null)
            {
                if (c.Accent.Length == 7 && c.Accent[0] == '#')
                {
                    return c.Accent.AsSpan(1).ToArray().All(Uri.IsHexDigit);
                }
                return false;
            }
            return true;
        }
        return false;
    }
}
