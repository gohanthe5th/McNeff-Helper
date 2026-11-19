using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

internal sealed class BuildSubStat
{
    internal readonly string Name;
    internal readonly string Icon;
    internal readonly string Value;

    internal BuildSubStat(string name, string icon, string value)
    {
        Name = name;
        Icon = icon;
        Value = value;
    }
}

internal static class BuildSubStats
{
    private static readonly Lazy<Rules> Catalog = new Lazy<Rules>(ReadRules);

    private sealed class Stat
    {
        internal string Name;
        internal int Format;
        internal string Icon;
    }

    private sealed class ColorRule
    {
        internal int Color;
        internal Stat Stat;
        internal int Operation;
        internal float Amount;
        internal float Cap;
    }

    private sealed class Rules
    {
        internal readonly Dictionary<int, float> ShapeWeights = new Dictionary<int, float>();
        internal readonly List<ColorRule> Colors = new List<ColorRule>();
        internal readonly List<string> Icons = new List<string>();
    }

    internal static IEnumerable<string> Icons
    {
        get { return Catalog.Value.Icons; }
    }

    internal static List<BuildSubStat> Calculate(IEnumerable<BuildPlacement> placements)
    {
        Rules rules = Catalog.Value;
        var weightByColor = new Dictionary<int, float>();
        foreach (BuildPlacement brick in placements)
        {
            float weight;
            if (!rules.ShapeWeights.TryGetValue(brick.Shape, out weight))
                continue;
            float total;
            weightByColor.TryGetValue(brick.Color, out total);
            weightByColor[brick.Color] = total + weight;
        }

        var stats = new List<BuildSubStat>();
        foreach (ColorRule rule in rules.Colors)
        {
            float weight;
            if (!weightByColor.TryGetValue(rule.Color, out weight))
                continue;
            float amount = rule.Amount * weight;
            if (rule.Cap > 0)
                amount = Math.Min(amount, rule.Cap);
            else if (rule.Cap < 0)
                amount = Math.Max(amount, rule.Cap);
            bool percent = rule.Operation == 2 || rule.Stat.Format == 1;
            double displayed = Math.Round(percent ? amount * 100f : amount, percent ? 1 : 2, MidpointRounding.AwayFromZero);
            if (displayed != 0)
            {
                string value = (displayed > 0 ? "+" : "") + displayed.ToString(percent ? "0.#" : "0.##", CultureInfo.CurrentCulture);
                stats.Add(new BuildSubStat(rule.Stat.Name, rule.Stat.Icon, value + (percent ? "%" : "")));
            }
        }
        return stats;
    }

    private static Rules ReadRules()
    {
        var rules = new Rules();
        var definitions = new Dictionary<int, Stat>();
        using (StreamReader reader = OpenResource("BuildStatCatalog.tsv"))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 4)
                    throw new InvalidDataException("A build sub-stat definition is invalid.");
                definitions.Add(ReadInt(fields[0]), new Stat {
                    Name = fields[1], Format = ReadInt(fields[2]), Icon = fields[3]
                });
                if (fields[3].Length > 0)
                    rules.Icons.Add(fields[3]);
            }
        }
        using (StreamReader reader = OpenResource("BuildStatRules.tsv"))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split('\t');
                if (fields.Length == 3 && fields[0] == "shape")
                    rules.ShapeWeights.Add(ReadInt(fields[1]), ReadAmount(fields[2]));
                else if (fields.Length == 6 && fields[0] == "color")
                    rules.Colors.Add(new ColorRule {
                        Color = ReadInt(fields[1]), Stat = definitions[ReadInt(fields[2])], Operation = ReadInt(fields[3]),
                        Amount = ReadAmount(fields[4]), Cap = ReadAmount(fields[5])
                    });
                else
                    throw new InvalidDataException("A brick sub-stat rule is invalid.");
            }
        }
        return rules;
    }

    private static StreamReader OpenResource(string name)
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream == null)
            throw new InvalidDataException("The build sub-stat catalog is missing.");
        return new StreamReader(stream);
    }

    private static int ReadInt(string value)
    {
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static float ReadAmount(string value)
    {
        return float.Parse(value, CultureInfo.InvariantCulture);
    }
}
