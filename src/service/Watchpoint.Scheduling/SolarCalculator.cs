namespace Watchpoint.Scheduling;

/// <summary>
/// NOAA Solar Position calculations (spec SCH §5) for sunrise and sunset (+-1 min accuracy).
/// Handles polar day / night (sun never sets or rises) by returning null.
/// </summary>
public static class SolarCalculator
{
    private const double Rad2Deg = 180.0 / Math.PI;
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Zenith = 90.8333;

    /// <summary>
    /// Calculates sunrise and sunset UTC times for a given latitude, longitude, and date.
    /// </summary>
    public static (DateTimeOffset? Sunrise, DateTimeOffset? Sunset) Calculate(
        double latitude,
        double longitude,
        DateOnly date)
    {
        double jd = GetJulianDay(date);
        double jc = (jd - 2451545.0) / 36525.0;

        double geomMeanLongSun = (280.46646 + jc * (36000.76983 + jc * 0.0003032)) % 360.0;
        if (geomMeanLongSun < 0)
        {
            geomMeanLongSun += 360.0;
        }

        double geomMeanAnomSun = 357.52911 + jc * (35999.05029 - 0.0001537 * jc);
        double eccentEarthOrbit = 0.016708634 - jc * (0.000042037 + 0.0000001267 * jc);

        double anomRad = geomMeanAnomSun * Deg2Rad;
        double sunEqOfCtr = Math.Sin(anomRad) * (1.914602 - jc * (0.004817 + 0.000014 * jc))
            + Math.Sin(2.0 * anomRad) * (0.019993 - 0.000101 * jc)
            + Math.Sin(3.0 * anomRad) * 0.000289;

        double sunTrueLong = geomMeanLongSun + sunEqOfCtr;
        double sunAppLong = sunTrueLong - 0.00569 - 0.00478 * Math.Sin((125.04 - 1934.136 * jc) * Deg2Rad);

        double meanObliqEcliptic = 23.0 + (26.0 + ((21.448 - jc * (46.815 + jc * (0.00059 - jc * 0.001813)))) / 60.0) / 60.0;
        double obliqCorr = meanObliqEcliptic + 0.00256 * Math.Cos((125.04 - 1934.136 * jc) * Deg2Rad);

        double obliqCorrRad = obliqCorr * Deg2Rad;
        double sunAppLongRad = sunAppLong * Deg2Rad;
        double sunDecline = Math.Asin(Math.Sin(obliqCorrRad) * Math.Sin(sunAppLongRad)) * Rad2Deg;

        double varY = Math.Tan(obliqCorrRad / 2.0) * Math.Tan(obliqCorrRad / 2.0);
        double geomMeanLongSunRad = geomMeanLongSun * Deg2Rad;

        double eqOfTime = 4.0 * Rad2Deg * (
            varY * Math.Sin(2.0 * geomMeanLongSunRad)
            - 2.0 * eccentEarthOrbit * Math.Sin(anomRad)
            + 4.0 * eccentEarthOrbit * varY * Math.Sin(anomRad) * Math.Cos(2.0 * geomMeanLongSunRad)
            - 0.5 * (varY * varY) * Math.Sin(4.0 * geomMeanLongSunRad)
            - 1.25 * (eccentEarthOrbit * eccentEarthOrbit) * Math.Sin(2.0 * anomRad));

        double latRad = latitude * Deg2Rad;
        double decRad = sunDecline * Deg2Rad;

        double cosHa = (Math.Cos(Zenith * Deg2Rad) / (Math.Cos(latRad) * Math.Cos(decRad)))
            - (Math.Tan(latRad) * Math.Tan(decRad));

        if (cosHa > 1.0 || cosHa < -1.0)
        {
            return (null, null);
        }

        double ha = Math.Acos(cosHa) * Rad2Deg;
        double solarNoonMin = 720.0 - 4.0 * longitude - eqOfTime;
        double sunriseMin = solarNoonMin - ha * 4.0;
        double sunsetMin = solarNoonMin + ha * 4.0;

        var midnightUtc = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
        var sunrise = midnightUtc.AddMinutes(sunriseMin);
        var sunset = midnightUtc.AddMinutes(sunsetMin);

        return (sunrise, sunset);
    }

    private static double GetJulianDay(DateOnly date)
    {
        int a = (14 - date.Month) / 12;
        int y = date.Year + 4800 - a;
        int m = date.Month + 12 * a - 3;
        return date.Day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045 - 0.5;
    }
}
