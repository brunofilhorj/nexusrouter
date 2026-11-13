namespace NexusRouter.SessionLog;

internal static class B3TradingCalendar
{
    /// <summary>
    /// Returns the trading-day key selected by the documented Drop Copy rollover windows.
    /// </summary>
    /// <param name="localTime">Current time in the configured BRT time zone.</param>
    /// <returns>The trading-day key as <c>yyyyMMdd</c>.</returns>
    public static int TradingDay(DateTime localTime)
    {
        DateTime local = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        for (int daysBack = 0; daysBack <= 7; daysBack++)
        {
            DateTime date = local.Date.AddDays(-daysBack);
            if (date.DayOfWeek is not (DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday)
                || local < date.AddHours(23))
            {
                continue;
            }

            return ToTradingDay(date.AddDays(1));
        }

        throw new InvalidOperationException("The B3 Drop Copy calendar did not find a recent trading-day rollover.");
    }

    /// <summary>
    /// Finds the retention boundary by counting prior Drop Copy trading-day keys.
    /// </summary>
    /// <param name="tradingDay">Current trading-day key as <c>yyyyMMdd</c>.</param>
    /// <param name="retentionTradingDays">Number of prior trading days to retain.</param>
    /// <returns>The oldest trading-day key that remains within retention.</returns>
    public static DateOnly RetentionCutoff(int tradingDay, int retentionTradingDays)
    {
        DateOnly cutoff = DateOnly.ParseExact(
            tradingDay.ToString("D8", System.Globalization.CultureInfo.InvariantCulture),
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture);

        int remaining = retentionTradingDays;
        while (remaining > 0)
        {
            cutoff = cutoff.AddDays(-1);
            if (cutoff.DayOfWeek is DayOfWeek.Wednesday or DayOfWeek.Thursday or DayOfWeek.Friday)
            {
                remaining--;
            }
        }

        return cutoff;
    }

    private static int ToTradingDay(DateTime date) =>
        date.Year * 10000 + date.Month * 100 + date.Day;
}
