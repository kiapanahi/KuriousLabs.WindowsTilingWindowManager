using System.Globalization;

namespace Bastion.TestWindows;

/// <summary>Parameters for one spawned test window (DESIGN.md §11).</summary>
internal sealed record TestWindowOptions(
    int Width,
    int Height,
    int MinWidth,
    int MinHeight,
    string Title)
{
    public static TestWindowOptions Default { get; } = new(
        Width: 800,
        Height: 600,
        MinWidth: 200,
        MinHeight: 150,
        Title: "Bastion Test Window");

    /// <summary>
    /// Parses <c>--width</c>/<c>--height</c>/<c>--min-width</c>/<c>--min-height</c>/<c>--title</c>
    /// from raw args, falling back to <see cref="Default"/> for anything unspecified. Deliberately
    /// hand-rolled rather than a <c>System.CommandLine</c> dependency: this is a subprocess-only
    /// test tool with five flat options, not a user-facing CLI.
    /// </summary>
    public static TestWindowOptions Parse(string[] args)
    {
        int width = Default.Width;
        int height = Default.Height;
        int minWidth = Default.MinWidth;
        int minHeight = Default.MinHeight;
        string title = Default.Title;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--width":
                    width = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--height":
                    height = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--min-width":
                    minWidth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--min-height":
                    minHeight = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--title":
                    title = args[++i];
                    break;
            }
        }

        return new TestWindowOptions(width, height, minWidth, minHeight, title);
    }
}
