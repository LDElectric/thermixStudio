namespace ThermixStudio.Core;

public enum ThermalPalette
{
    Iron = 1,
    Rainbow = 2,
    Grayscale = 3,
    Hotmetal = 4,
    Arctic = 5,
    Thermal = 6,
    Jet = 7,
    Hot = 8,
    Cool = 9,
    Original = 99
}

public enum ImageViewMode
{
    Original = 0,
    Thermal = 1,
    Visible = 2,
    Fusion = 3,
    Blending = 4,
    PiP = 5,
    Msx = 6
}

public enum ThermalCameraBrand
{
    Unknown = 0,
    Flir = 1,
    Fluke = 2,
    Hikvision = 3,
    InfiRay = 4,
    Guide = 5,
    Bosch = 6,
    Seek = 7,
    Testo = 8,
    Generic = 99
}

public enum VisualScaleSource
{
    Unknown = 0,
    BurnedInScale = 1,
    VisualFitToReference = 2,
    ExifImageTemperature = 3,
    MatrixRange = 4,
    Manual = 5
}
