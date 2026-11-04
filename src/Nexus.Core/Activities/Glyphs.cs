namespace Nexus.Core.Activities;

/// <summary>
/// Code points in the Windows system icon fonts ("Segoe Fluent Icons" on Windows 11, falling back to
/// "Segoe MDL2 Assets" on Windows 10). These are vector system icons, legally safe and always installed.
/// </summary>
public static class Glyphs
{
    public const string Play = "\uE768";
    public const string Pause = "\uE769";
    public const string Previous = "\uE892";
    public const string Next = "\uE893";
    public const string Music = "\uE8D6";

    public const string Mute = "\uE74F";
    public const string Volume0 = "\uE992";
    public const string Volume1 = "\uE993";
    public const string Volume2 = "\uE994";
    public const string Volume3 = "\uE995";
    public const string Speakers = "\uE7F5";
    public const string Headphones = "\uE7F6";
    public const string Display = "\uE7F4";

    public const string Microphone = "\uE720";
    public const string MicOff = "\uEC54";
    public const string Camera = "\uE714";

    public const string Bluetooth = "\uE702";
    public const string Wifi = "\uE701";
    public const string Ethernet = "\uE839";
    public const string Cellular = "\uEC3B";
    public const string Vpn = "\uE705";
    public const string Offline = "\uF384";
    public const string Globe = "\uE774";

    public const string Lightning = "\uE945";
    public const string BatterySaver = "\uE863";

    public const string Calendar = "\uE787";
    public const string Video = "\uE714";
    public const string People = "\uE716";
    public const string Ringer = "\uEA8F";
    public const string Message = "\uE8BD";
    public const string QuietHours = "\uE708";

    public const string Download = "\uE896";
    public const string Folder = "\uE8B7";
    public const string Document = "\uE8A5";
    public const string Shelf = "\uE7B8"; // "Package": a box you put things in
    public const string Screenshot = "\uE722";
    public const string Copy = "\uE8C8";

    public const string Settings = "\uE713";
    public const string Close = "\uE711";
    public const string Open = "\uE8A7";
    public const string Warning = "\uE7BA";
    public const string Info = "\uE946";
    public const string Clock = "\uE917";
    public const string Power = "\uE7E8";
    public const string Pin = "\uE718";
    public const string Grid = "\uF0E2";
    public const string Sliders = "\uE9E9";
    public const string Shield = "\uEA18";
    public const string Plug = "\uE83E";

    /// <summary>Battery glyph for a percentage. Windows ships 11 discrete levels for each state.</summary>
    public static string Battery(int percent, bool charging)
    {
        int level = System.Math.Clamp((int)System.Math.Round(percent / 10.0), 0, 10);
        if (charging) return level == 10 ? "\uE83E" : ((char)(0xE85A + level)).ToString();
        return level == 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();
    }

    public static string VolumeFor(float level, bool muted)
    {
        if (muted || level <= 0.001f) return Mute;
        if (level < 0.34f) return Volume1;
        if (level < 0.67f) return Volume2;
        return Volume3;
    }

    public static string WifiBars(int? bars) => bars switch
    {
        null => Wifi,
        <= 1 => "\uE872",
        2 => "\uE873",
        3 => "\uE874",
        _ => Wifi,
    };
}
