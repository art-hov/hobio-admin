namespace HobioAdmin.Web.Analytics;

/// <summary>Headline numbers for one reporting window.</summary>
public sealed class KpiSnapshot
{
    public int NewUsers { get; set; }
    public int TotalUsers { get; set; }
    public int EventsCreated { get; set; }
    public int EventsHeld { get; set; }
    public int PaidEventsHeld { get; set; }
    public int ActiveHosts { get; set; }
    public long SlotsOffered { get; set; }
    public long SlotsBooked { get; set; }
    public decimal? AvgPaidPrice { get; set; }
    public int Bookings { get; set; }
    public int UniqueBookers { get; set; }
    public int RepeatBookers { get; set; }
    public int PaidTransactions { get; set; }
    public decimal GrossBookingValue { get; set; }
    public decimal HobioFeeRevenue { get; set; }
    public decimal StripeFeesCollected { get; set; }
    public decimal HostEarnings { get; set; }
    public int RefundedCount { get; set; }
    public decimal RefundedAmount { get; set; }
    public decimal PayoutsCompletedAmount { get; set; }

    /// <summary>Booked share of offered spots in events held, 0..1.</summary>
    public double? FillRate => SlotsOffered > 0 ? (double)SlotsBooked / SlotsOffered : null;

    /// <summary>Share of players who booked at least twice in the window, 0..1.</summary>
    public double? RepeatBookerRate => UniqueBookers > 0 ? (double)RepeatBookers / UniqueBookers : null;

    /// <summary>Hobio fee revenue as a share of gross booking value, 0..1.</summary>
    public double? TakeRate => GrossBookingValue > 0 ? (double)(HobioFeeRevenue / GrossBookingValue) : null;

    public double? BookingsPerEvent => EventsHeld > 0 ? (double)Bookings / EventsHeld : null;
}

/// <summary>One day's value from the database (local Toronto date).</summary>
public sealed class DailyValue
{
    public DateTime Day { get; set; }
    public decimal Value { get; set; }
}

/// <summary>One day's sum and count, for averages that must be weighted when grouped into buckets.</summary>
public sealed class DailySumCount
{
    public DateTime Day { get; set; }
    public decimal Total { get; set; }
    public int Count { get; set; }
}

public sealed class DailyRevenue
{
    public DateTime Day { get; set; }
    public decimal Gross { get; set; }
    public decimal HobioFees { get; set; }
}

public sealed class WeekdayCityCount
{
    public int? CityId { get; set; }
    public string CityName { get; set; } = string.Empty;
    public int IsoWeekday { get; set; }
    public int Events { get; set; }
    public long BookedSlots { get; set; }
}

public sealed class CitySummary
{
    public int CityId { get; set; }
    public string CityName { get; set; } = string.Empty;
    public string? CountryName { get; set; }
    public int EventsHeld { get; set; }
    public int ActiveHosts { get; set; }
    public long SlotsOffered { get; set; }
    public long SlotsBooked { get; set; }
    public decimal? AvgPaidPrice { get; set; }
    public int Bookings { get; set; }
    public int Players { get; set; }
    public decimal GrossBookingValue { get; set; }
    public decimal HobioFeeRevenue { get; set; }

    public double? FillRate => SlotsOffered > 0 ? (double)SlotsBooked / SlotsOffered : null;
}

public sealed class MonthlyActive
{
    public DateTime Month { get; set; }
    public int ActiveHosts { get; set; }
    public int ActivePlayers { get; set; }
}

public sealed class CohortRow
{
    public DateTime CohortMonth { get; set; }
    public int CohortSize { get; set; }
    public int Month0 { get; set; }
    public int Month1 { get; set; }
    public int Month2 { get; set; }
    public int Month3 { get; set; }
}

public sealed class CityOption
{
    public int CityId { get; set; }
    public string CityName { get; set; } = string.Empty;
}
