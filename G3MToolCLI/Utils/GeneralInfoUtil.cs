using System;
using G3MLib.DataFile;
using G3MLib.DataFile.Models;
using G3MToolCLI.Models;

namespace G3MToolCLI.Utils;

public static class GeneralInfoUtil
{
    public static GeneralInfoData? ExtractGeneralInfo(GameMakerData data)
    {
        GameMakerGeneralInfo gi = data.GeneralInfo;
        if (gi == null)
        {
            return null;
        }
        return new GeneralInfoData
        {
            DisplayName = gi.DisplayName?.Content,
            Name = gi.Name?.Content,
            FileName = gi.FileName?.Content,
            Config = gi.Config?.Content,
            GameID = gi.GameID,
            DirectPlayGuid = gi.DirectPlayGuid.ToString(),
            Major = gi.Major,
            Minor = gi.Minor,
            Release = gi.Release,
            Build = gi.Build,
            DefaultWindowWidth = gi.DefaultWindowWidth,
            DefaultWindowHeight = gi.DefaultWindowHeight,
            InfoFlags = (uint)gi.Info,
            LicenseCRC32 = (int)gi.LicenseCRC32,
            Timestamp = gi.Timestamp,
            ActiveTargets = gi.ActiveTargets,
            FunctionClassifications = (ulong)gi.FunctionClassifications,
            SteamAppID = gi.SteamAppID,
            DebuggerPort = (int)((gi.BytecodeVersion >= 14) ? gi.DebuggerPort : 0),
            GMS2FPS = ((gi.Major >= 2) ? gi.GMS2FPS : 0f),
            GMS2AllowStatistics = (gi.Major >= 2 && gi.GMS2AllowStatistics),
            RoomOrderCount = (gi.RoomOrder?.Count ?? 0)
        };
    }

    public static string GetVersionDisplay(GameMakerGeneralInfo? info)
    {
        if (info == null)
        {
            return "Unknown";
        }
        string rawVersion = $"{info.Major}.{info.Minor}.{info.Release}.{info.Build}";
        string interpretedVersion;
        if (info.Branch == GameMakerGeneralInfo.BranchType.LTS2022_0)
        {
            interpretedVersion = "2022.0";
        }
        else
        {
            if (info.Major == 1)
            {
                return "GMS " + rawVersion;
            }
            interpretedVersion = $"{info.Major}.{info.Minor}";
            if (info.Release != 0)
            {
                interpretedVersion += $".{info.Release}";
                if (info.Build != 0)
                {
                    interpretedVersion += $".{info.Build}";
                }
            }
        }
        string prefix = ((info.Major < 2022 || (info.Major == 2022 && info.Minor < 3)) ? "GMS" : "GM");
        if (rawVersion != interpretedVersion && info.Branch == GameMakerGeneralInfo.BranchType.LTS2022_0)
        {
            return $"{prefix} {interpretedVersion} (raw: {rawVersion})";
        }
        return prefix + " " + interpretedVersion;
    }

    public static void PrintVerboseGeneralInfo(GameMakerGeneralInfo gi)
    {
        Console.WriteLine();
        Console.WriteLine("General Info (detailed):");
        Console.WriteLine("  Display Name: " + (gi.DisplayName?.Content ?? "N/A"));
        Console.WriteLine("  Internal Name: " + (gi.Name?.Content ?? "N/A"));
        Console.WriteLine("  File Name: " + (gi.FileName?.Content ?? "N/A"));
        Console.WriteLine("  Config: " + (gi.Config?.Content ?? "N/A"));
        Console.WriteLine($"  Game ID: {gi.GameID}");
        Console.WriteLine($"  DirectPlay GUID: {gi.DirectPlayGuid}");
        Console.WriteLine();
        Console.WriteLine("Version:");
        Console.WriteLine($"  Major: {gi.Major}");
        Console.WriteLine($"  Minor: {gi.Minor}");
        Console.WriteLine($"  Release: {gi.Release}");
        Console.WriteLine($"  Build: {gi.Build}");
        Console.WriteLine($"  Bytecode Version: {gi.BytecodeVersion}");
        Console.WriteLine($"  Branch: {gi.Branch}");
        Console.WriteLine();
        Console.WriteLine("Window:");
        Console.WriteLine($"  Default Width: {gi.DefaultWindowWidth}");
        Console.WriteLine($"  Default Height: {gi.DefaultWindowHeight}");
        Console.WriteLine();
        Console.WriteLine("Flags:");
        PrintInfoFlags(gi.Info);
        Console.WriteLine();
        Console.WriteLine("Other:");
        Console.WriteLine($"  Steam App ID: {gi.SteamAppID}");
        Console.WriteLine($"  Timestamp: {gi.Timestamp}");
        Console.WriteLine($"  License CRC32: {gi.LicenseCRC32}");
        Console.WriteLine($"  Active Targets: {gi.ActiveTargets}");
        Console.WriteLine($"  Room Order Count: {gi.RoomOrder?.Count ?? 0}");
        if (gi.BytecodeVersion >= 14)
        {
            Console.WriteLine($"  Debugger Port: {gi.DebuggerPort}");
            Console.WriteLine($"  Debugger Disabled: {gi.IsDebuggerDisabled}");
        }
        if (gi.Major >= 2)
        {
            Console.WriteLine($"  GMS2 FPS: {gi.GMS2FPS}");
            Console.WriteLine($"  GMS2 Allow Statistics: {gi.GMS2AllowStatistics}");
        }
    }

    private static void PrintInfoFlags(GameMakerGeneralInfo.InfoFlags flags)
    {
        Console.WriteLine($"  Fullscreen: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.Fullscreen)}");
        Console.WriteLine($"  Interpolate: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.Interpolate)}");
        Console.WriteLine($"  Scale: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.Scale)}");
        Console.WriteLine($"  Show Cursor: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.ShowCursor)}");
        Console.WriteLine($"  Sizeable: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.Sizeable)}");
        Console.WriteLine($"  Steam Enabled: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.SteamEnabled)}");
        Console.WriteLine($"  Borderless Window: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.BorderlessWindow)}");
        Console.WriteLine($"  Use AppData Save Location: {flags.HasFlag(GameMakerGeneralInfo.InfoFlags.UseAppDataSaveLocation)}");
    }
}
