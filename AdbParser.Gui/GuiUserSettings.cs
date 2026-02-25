namespace AdbParser.Gui;

public sealed class GuiUserSettings
{
    public int Version { get; set; } = 1;

    public int ResolutionIndex { get; set; } = 0;
    public int BitrateIndex { get; set; } = 1;
    public int RenderFpsIndex { get; set; } = 0;
    public int ViewModeIndex { get; set; } = 0;
    public int ToolsCategoryIndex { get; set; } = 0;

    public string? PreferredDeviceSerial { get; set; }

    public string? ShellCommandText { get; set; }
    public string? InputText { get; set; }
    public string? TapX { get; set; }
    public string? TapY { get; set; }

    public double? ToolboxWidth { get; set; }
    public double? OutputHeight { get; set; }

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowPosX { get; set; }
    public int? WindowPosY { get; set; }
    public string? WindowStateName { get; set; }
}
