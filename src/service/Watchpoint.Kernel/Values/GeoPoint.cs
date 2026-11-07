namespace Watchpoint.Kernel.Values;

/// <summary>A WGS 84 position.</summary>
/// <param name="Latitude">Latitude in degrees, −90 to 90.</param>
/// <param name="Longitude">Longitude in degrees, −180 to 180.</param>
/// <param name="Altitude">Altitude in metres, or <see langword="null"/>.</param>
public readonly record struct GeoPoint(double Latitude, double Longitude, double? Altitude);
