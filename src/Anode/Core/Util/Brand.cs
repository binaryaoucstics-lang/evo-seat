using System.Text.RegularExpressions;

namespace Anode.Core.Util;

internal static class Brand
{
    public const string Name = "EVO Seat";
    public const string Command = "evo-seat";
    public const string Channel = "proto-dual";

    public static string Text(string text) => Regex.Replace(text, @"(?<![\\/\w])Anode(?![\w.-])", Name);
    public static string Help(string text) => Text(text).Replace("anode ", Command + " ", StringComparison.Ordinal);
}
