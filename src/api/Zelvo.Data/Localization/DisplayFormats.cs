namespace Zelvo.Data.Localization;

/// <summary>Calendar used to show dates (LANG-8).</summary>
public enum CalendarSystem
{
    /// <summary>Gregorian calendar.</summary>
    Gregorian,

    /// <summary>Hijri calendar (Umm al-Qura).</summary>
    Hijri,
}

/// <summary>Digits used to show numbers and dates (LANG-8).</summary>
public enum DigitSystem
{
    /// <summary>Western digits: 0123456789.</summary>
    Western,

    /// <summary>Arabic-Indic digits: ٠١٢٣٤٥٦٧٨٩.</summary>
    ArabicIndic,
}
