using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Scheduling;

/// <summary>
/// Shipped presets for country public holidays (spec SCH §7).
/// Supports FR, MA, DZ, TN, AE, SA, QA, US, GB, DE, ES, TR for current and next year.
/// Islamic holidays are marked "date to confirm" because they depend on moon sighting.
/// </summary>
public static class HolidayPresets
{
    /// <summary>The supported country codes (ISO 3166-1 alpha-2).</summary>
    public static readonly IReadOnlyList<string> SupportedCountries =
        ["FR", "MA", "DZ", "TN", "AE", "SA", "QA", "US", "GB", "DE", "ES", "TR"];

    /// <summary>Gets a preset holiday set for the given country code.</summary>
    /// <param name="countryCode">ISO 3166-1 alpha-2 code.</param>
    /// <param name="year">Base year (defaults to 2026).</param>
    /// <returns>The HolidaySet preset.</returns>
    public static HolidaySet GetPreset(string countryCode, int year = 2026)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(countryCode);
        var upper = countryCode.ToUpperInvariant();
        if (!SupportedCountries.Contains(upper))
        {
            throw new ArgumentException($"Country code '{countryCode}' is not supported for presets", nameof(countryCode));
        }

        var holidays = new List<Holiday>();
        var years = new[] { year, year + 1 };

        switch (upper)
        {
            case "FR":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("New Year's Day", new Dictionary<string, string> { ["fr"] = "Jour de l'an" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 1), new LocalizedName("Labour Day", new Dictionary<string, string> { ["fr"] = "Fête du Travail" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 8), new LocalizedName("Victory in Europe Day", new Dictionary<string, string> { ["fr"] = "Victoire 1945" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 7, 14), new LocalizedName("Bastille Day", new Dictionary<string, string> { ["fr"] = "Fête Nationale" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 8, 15), new LocalizedName("Assumption of Mary", new Dictionary<string, string> { ["fr"] = "Assomption" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 11, 1), new LocalizedName("All Saints' Day", new Dictionary<string, string> { ["fr"] = "Toussaint" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 11, 11), new LocalizedName("Armistice Day", new Dictionary<string, string> { ["fr"] = "Armistice" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 25), new LocalizedName("Christmas Day", new Dictionary<string, string> { ["fr"] = "Noël" }), true));
                break;

            case "MA":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("New Year's Day", new Dictionary<string, string> { ["fr"] = "Jour de l'an", ["ar"] = "رأس السنة الميلادية" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 1, 11), new LocalizedName("Proclamation of Independence", new Dictionary<string, string> { ["fr"] = "Manifeste de l'Indépendance", ["ar"] = "تقديم وثيقة الاستقلال" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 1), new LocalizedName("Labour Day", new Dictionary<string, string> { ["fr"] = "Fête du Travail", ["ar"] = "عيد الشغل" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 7, 30), new LocalizedName("Throne Day", new Dictionary<string, string> { ["fr"] = "Fête du Trône", ["ar"] = "عيد العرش" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 8, 14), new LocalizedName("Oued Ed-Dahab Day", new Dictionary<string, string> { ["fr"] = "Allégeance Oued Ed-Dahab", ["ar"] = "استرجاع وادي الذهب" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 8, 20), new LocalizedName("Revolution of the King and the People", new Dictionary<string, string> { ["fr"] = "Révolution du Roi et du Peuple", ["ar"] = "ثورة الملك والشعب" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 8, 21), new LocalizedName("Youth Day", new Dictionary<string, string> { ["fr"] = "Fête de la Jeunesse", ["ar"] = "عيد الشباب" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 11, 6), new LocalizedName("Green March Day", new Dictionary<string, string> { ["fr"] = "Marche Verte", ["ar"] = "المسيرة الخضراء" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 11, 18), new LocalizedName("Independence Day", new Dictionary<string, string> { ["fr"] = "Fête de l'Indépendance", ["ar"] = "عيد الاستقلال" }), true));

                foreach (var y in years)
                {
                    holidays.Add(new Holiday(new DateOnly(y, 3, 20), new LocalizedName("Eid al-Fitr (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الفطر (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 27), new LocalizedName("Eid al-Adha (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الأضحى (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 6, 17), new LocalizedName("Hijri New Year (date to confirm)", new Dictionary<string, string> { ["ar"] = "فاتح محرم (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 8, 26), new LocalizedName("Mawlid (date to confirm)", new Dictionary<string, string> { ["ar"] = "المولد النبوي (تاريخ للتأكيد)" }), false));
                }
                break;

            case "DZ":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("New Year's Day", new Dictionary<string, string> { ["fr"] = "Jour de l'an", ["ar"] = "رأس السنة الميلادية" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 1, 12), new LocalizedName("Yennayer (Berber New Year)", new Dictionary<string, string> { ["fr"] = "Yennayer", ["ar"] = "رأس السنة الأمازيغية" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 1), new LocalizedName("Labour Day", new Dictionary<string, string> { ["fr"] = "Fête du Travail", ["ar"] = "عيد العمال" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 7, 5), new LocalizedName("Independence Day", new Dictionary<string, string> { ["fr"] = "Fête de l'Indépendance", ["ar"] = "عيد الاستقلال" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 11, 1), new LocalizedName("Revolution Day", new Dictionary<string, string> { ["fr"] = "Fête de la Révolution", ["ar"] = "عيد الثورة" }), true));
                foreach (var y in years)
                {
                    holidays.Add(new Holiday(new DateOnly(y, 3, 20), new LocalizedName("Eid al-Fitr (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الفطر (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 27), new LocalizedName("Eid al-Adha (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الأضحى (تاريخ للتأكيد)" }), false));
                }
                break;

            case "TN":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("New Year's Day", new Dictionary<string, string> { ["fr"] = "Jour de l'an" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 3, 20), new LocalizedName("Independence Day", new Dictionary<string, string> { ["fr"] = "Fête de l'Indépendance" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 4, 9), new LocalizedName("Martyrs' Day", new Dictionary<string, string> { ["fr"] = "Fête des Martyrs" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 1), new LocalizedName("Labour Day", new Dictionary<string, string> { ["fr"] = "Fête du Travail" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 7, 25), new LocalizedName("Republic Day", new Dictionary<string, string> { ["fr"] = "Fête de la République" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 8, 13), new LocalizedName("Women's Day", new Dictionary<string, string> { ["fr"] = "Fête de la Femme" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 10, 15), new LocalizedName("Evacuation Day", new Dictionary<string, string> { ["fr"] = "Fête de l'Évacuation" }), true));
                foreach (var y in years)
                {
                    holidays.Add(new Holiday(new DateOnly(y, 3, 20), new LocalizedName("Eid al-Fitr (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الفطر (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 27), new LocalizedName("Eid al-Adha (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الأضحى (تاريخ للتأكيد)" }), false));
                }
                break;

            case "AE":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("New Year's Day", new Dictionary<string, string> { ["ar"] = "رأس السنة الميلادية" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 2), new LocalizedName("National Day", new Dictionary<string, string> { ["ar"] = "اليوم الوطني" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 3), new LocalizedName("National Day Holiday", new Dictionary<string, string> { ["ar"] = "عطلة اليوم الوطني" }), true));
                foreach (var y in years)
                {
                    holidays.Add(new Holiday(new DateOnly(y, 3, 20), new LocalizedName("Eid al-Fitr (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الفطر (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 27), new LocalizedName("Arafat Day (date to confirm)", new Dictionary<string, string> { ["ar"] = "يوم عرفة (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 28), new LocalizedName("Eid al-Adha (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الأضحى (تاريخ للتأكيد)" }), false));
                }
                break;

            case "SA":
                holidays.Add(new Holiday(new DateOnly(year, 2, 22), new LocalizedName("Founding Day", new Dictionary<string, string> { ["ar"] = "يوم التأسيس" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 9, 23), new LocalizedName("National Day", new Dictionary<string, string> { ["ar"] = "اليوم الوطني" }), true));
                foreach (var y in years)
                {
                    holidays.Add(new Holiday(new DateOnly(y, 3, 20), new LocalizedName("Eid al-Fitr (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الفطر (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 27), new LocalizedName("Eid al-Adha (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الأضحى (تاريخ للتأكيد)" }), false));
                }
                break;

            case "QA":
                holidays.Add(new Holiday(new DateOnly(year, 12, 18), new LocalizedName("National Day", new Dictionary<string, string> { ["ar"] = "اليوم الوطني" }), true));
                foreach (var y in years)
                {
                    holidays.Add(new Holiday(new DateOnly(y, 3, 20), new LocalizedName("Eid al-Fitr (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الفطر (تاريخ للتأكيد)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 27), new LocalizedName("Eid al-Adha (date to confirm)", new Dictionary<string, string> { ["ar"] = "عيد الأضحى (تاريخ للتأكيد)" }), false));
                }
                break;

            case "US":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), LocalizedName.Of("New Year's Day"), true));
                holidays.Add(new Holiday(new DateOnly(year, 6, 19), LocalizedName.Of("Juneteenth"), true));
                holidays.Add(new Holiday(new DateOnly(year, 7, 4), LocalizedName.Of("Independence Day"), true));
                holidays.Add(new Holiday(new DateOnly(year, 11, 11), LocalizedName.Of("Veterans Day"), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 25), LocalizedName.Of("Christmas Day"), true));
                break;

            case "GB":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), LocalizedName.Of("New Year's Day"), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 25), LocalizedName.Of("Christmas Day"), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 26), LocalizedName.Of("Boxing Day"), true));
                break;

            case "DE":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("Neujahr", new Dictionary<string, string> { ["de"] = "Neujahr" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 1), new LocalizedName("Tag der Arbeit", new Dictionary<string, string> { ["de"] = "Tag der Arbeit" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 10, 3), new LocalizedName("Tag der Deutschen Einheit", new Dictionary<string, string> { ["de"] = "Tag der Deutschen Einheit" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 25), new LocalizedName("1. Weihnachtstag", new Dictionary<string, string> { ["de"] = "1. Weihnachtstag" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 26), new LocalizedName("2. Weihnachtstag", new Dictionary<string, string> { ["de"] = "2. Weihnachtstag" }), true));
                break;

            case "ES":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("Año Nuevo", new Dictionary<string, string> { ["es"] = "Año Nuevo" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 1, 6), new LocalizedName("Epifanía del Señor", new Dictionary<string, string> { ["es"] = "Epifanía del Señor" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 1), new LocalizedName("Fiesta del Trabajo", new Dictionary<string, string> { ["es"] = "Fiesta del Trabajo" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 8, 15), new LocalizedName("Asunción de la Virgen", new Dictionary<string, string> { ["es"] = "Asunción de la Virgen" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 10, 12), new LocalizedName("Fiesta Nacional de España", new Dictionary<string, string> { ["es"] = "Fiesta Nacional de España" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 11, 1), new LocalizedName("Todos los Santos", new Dictionary<string, string> { ["es"] = "Todos los Santos" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 6), new LocalizedName("Día de la Constitución", new Dictionary<string, string> { ["es"] = "Día de la Constitución" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 8), new LocalizedName("Inmaculada Concepción", new Dictionary<string, string> { ["es"] = "Inmaculada Concepción" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 12, 25), new LocalizedName("Navidad", new Dictionary<string, string> { ["es"] = "Navidad" }), true));
                break;

            case "TR":
                holidays.Add(new Holiday(new DateOnly(year, 1, 1), new LocalizedName("New Year's Day", new Dictionary<string, string> { ["tr"] = "Yılbaşı" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 4, 23), new LocalizedName("National Sovereignty and Children's Day", new Dictionary<string, string> { ["tr"] = "Ulusal Egemenlik ve Çocuk Bayramı" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 1), new LocalizedName("Labour and Solidarity Day", new Dictionary<string, string> { ["tr"] = "Emek ve Dayanışma Günü" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 5, 19), new LocalizedName("Commemoration of Atatürk, Youth and Sports Day", new Dictionary<string, string> { ["tr"] = "Atatürk'ü Anma, Gençlik ve Spor Bayramı" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 7, 15), new LocalizedName("Democracy and National Unity Day", new Dictionary<string, string> { ["tr"] = "Demokrasi ve Milli Birlik Günü" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 8, 30), new LocalizedName("Victory Day", new Dictionary<string, string> { ["tr"] = "Zafer Bayramı" }), true));
                holidays.Add(new Holiday(new DateOnly(year, 10, 29), new LocalizedName("Republic Day", new Dictionary<string, string> { ["tr"] = "Cumhuriyet Bayramı" }), true));
                foreach (var y in years)
                {
                    holidays.Add(new Holiday(new DateOnly(y, 3, 20), new LocalizedName("Ramazan Bayramı (date to confirm)", new Dictionary<string, string> { ["tr"] = "Ramazan Bayramı (tarih onaylanacak)" }), false));
                    holidays.Add(new Holiday(new DateOnly(y, 5, 27), new LocalizedName("Kurban Bayramı (date to confirm)", new Dictionary<string, string> { ["tr"] = "Kurban Bayramı (tarih onaylanacak)" }), false));
                }
                break;
        }

        var setName = new LocalizedName($"{upper} Public Holidays", new Dictionary<string, string>());
        return new HolidaySet(HolidaySetId.New(), setName, upper, holidays, 1);
    }
}
