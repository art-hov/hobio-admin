/*
    Hobio Admin: analytics views over the Tushpa database.

    Run against the Tushpa database (dev first, then prod) with a login that can create schemas and views.
    Safe to re-run: every object uses CREATE OR ALTER.

    The views expose aggregate-friendly columns only: no names, emails, phone numbers or payment IDs.
    The portal's read-only login can read these views and nothing else (see 02_readonly_user.sql).

    Assumptions (check against Tushpa if numbers look off):
      - All *CreatedAt*, Events.StartDate and EventBookings.BookedAt values are UTC.
        (RDS SQL Server runs in UTC, so GETDATE() defaults are UTC too.)
      - Events.TimeZoneId holds an IANA id such as 'America/Toronto' or a Windows id such as
        'Eastern Standard Time'. Unknown values fall back to the city's time zone, then to Toronto.
      - "Local date" for created/booked timestamps uses Toronto time (Hobio's home market).

    Note: Tushpa's dacpac deploy (sqlpackage, default DropObjectsNotInSource=false) leaves this schema alone.
*/

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'analytics')
    EXEC (N'CREATE SCHEMA [analytics] AUTHORIZATION [dbo]');
GO

/* Maps IANA time zone ids to the Windows ids SQL Server's AT TIME ZONE understands. */
CREATE OR ALTER VIEW [analytics].[vw_TimeZoneMap]
AS
SELECT v.IanaId, v.WindowsId
FROM (VALUES
    (N'America/Toronto',      N'Eastern Standard Time'),
    (N'America/Montreal',     N'Eastern Standard Time'),
    (N'America/New_York',     N'Eastern Standard Time'),
    (N'America/Detroit',      N'Eastern Standard Time'),
    (N'America/Nipigon',      N'Eastern Standard Time'),
    (N'America/Thunder_Bay',  N'Eastern Standard Time'),
    (N'America/Iqaluit',      N'Eastern Standard Time'),
    (N'America/Halifax',      N'Atlantic Standard Time'),
    (N'America/Moncton',      N'Atlantic Standard Time'),
    (N'America/St_Johns',     N'Newfoundland Standard Time'),
    (N'America/Winnipeg',     N'Central Standard Time'),
    (N'America/Chicago',      N'Central Standard Time'),
    (N'America/Regina',       N'Canada Central Standard Time'),
    (N'America/Edmonton',     N'Mountain Standard Time'),
    (N'America/Denver',       N'Mountain Standard Time'),
    (N'America/Vancouver',    N'Pacific Standard Time'),
    (N'America/Los_Angeles',  N'Pacific Standard Time'),
    (N'Europe/London',        N'GMT Standard Time'),
    (N'Asia/Yerevan',         N'Caucasus Standard Time'),
    (N'UTC',                  N'UTC'),
    (N'Etc/UTC',              N'UTC')
) AS v (IanaId, WindowsId);
GO

CREATE OR ALTER VIEW [analytics].[vw_Users]
AS
SELECT
    u.[Id]                                                                      AS UserId,
    u.[CreatedAt]                                                               AS CreatedAtUtc,
    CAST((u.[CreatedAt] AT TIME ZONE 'UTC') AT TIME ZONE 'Eastern Standard Time' AS date) AS CreatedDateLocal,
    u.[IsDeleted]                                                               AS IsDeleted,
    CAST(CASE WHEN u.[ReferredByUserId] IS NOT NULL THEN 1 ELSE 0 END AS bit)   AS WasReferred
FROM [dbo].[Users] AS u;
GO

CREATE OR ALTER VIEW [analytics].[vw_Events]
AS
SELECT
    e.[Id]                                                                      AS EventId,
    e.[CreatedByUserId]                                                         AS HostUserId,
    e.[CreatedAt]                                                               AS CreatedAtUtc,
    CAST((e.[CreatedAt] AT TIME ZONE 'UTC') AT TIME ZONE 'Eastern Standard Time' AS date) AS CreatedDateLocal,
    e.[StartDate]                                                               AS StartAtUtc,
    st.StartAtLocal                                                             AS StartAtLocal,
    CAST(st.StartAtLocal AS date)                                               AS StartDateLocal,
    -- 1 = Monday ... 7 = Sunday, independent of the server's DATEFIRST/language settings (1900-01-01 was a Monday).
    (DATEDIFF(day, CAST('19000101' AS date), CAST(st.StartAtLocal AS date)) % 7) + 1 AS StartIsoWeekday,
    DATEPART(hour, st.StartAtLocal)                                             AS StartHourLocal,
    e.[DurationMinutes]                                                         AS DurationMinutes,
    e.[EventType]                                                               AS EventType,      -- 1 OneTime, 2 Recurring
    e.[EventStatus]                                                             AS EventStatus,    -- 1 Draft ... 4 Cancelled ... 7 Archived
    CAST(CASE WHEN e.[EventStatus] = 4 THEN 1 ELSE 0 END AS bit)                AS IsCancelled,
    CAST(CASE WHEN e.[EventStatus] = 1 THEN 1 ELSE 0 END AS bit)                AS IsDraft,
    e.[IsPaidEvent]                                                             AS IsPaidEvent,
    CASE WHEN e.[IsPaidEvent] = 1 THEN e.[Price] END                            AS Price,
    e.[TotalAvailableSlots]                                                     AS TotalAvailableSlots,
    e.[BookedSlots]                                                             AS BookedSlots,
    e.[SportId]                                                                 AS SportId,
    s.[Name]                                                                    AS SportName,
    v.[CityId]                                                                  AS CityId,
    c.[Name]                                                                    AS CityName,
    co.[Name]                                                                   AS CountryName,
    st.WindowsTimeZone                                                          AS WindowsTimeZone
FROM [dbo].[Events] AS e
INNER JOIN [dbo].[Venues]    AS v  ON v.[Id]  = e.[VenueId]
INNER JOIN [dbo].[Cities]    AS c  ON c.[Id]  = v.[CityId]
LEFT  JOIN [dbo].[Countries] AS co ON co.[Id] = c.[CountryId]
LEFT  JOIN [dbo].[Sports]    AS s  ON s.[Id]  = e.[SportId]
LEFT  JOIN [analytics].[vw_TimeZoneMap] AS me ON me.IanaId = CAST(e.[TimeZoneId] AS nvarchar(128)) COLLATE DATABASE_DEFAULT
LEFT  JOIN [analytics].[vw_TimeZoneMap] AS mc ON mc.IanaId = CAST(c.[TimeZoneId] AS nvarchar(128)) COLLATE DATABASE_DEFAULT
OUTER APPLY (
    -- First time zone id that SQL Server recognises, in priority order: event, mapped event, city, mapped city.
    SELECT TOP (1) z.[name] AS ZoneName
    FROM (VALUES (1, e.[TimeZoneId]), (2, me.WindowsId), (3, c.[TimeZoneId]), (4, mc.WindowsId)) AS cand (Priority, ZoneId)
    INNER JOIN sys.time_zone_info AS z ON z.[name] COLLATE DATABASE_DEFAULT = CAST(cand.ZoneId AS nvarchar(128)) COLLATE DATABASE_DEFAULT
    ORDER BY cand.Priority
) AS tz
CROSS APPLY (
    SELECT
        COALESCE(tz.ZoneName, N'Eastern Standard Time') AS WindowsTimeZone,
        CAST((e.[StartDate] AT TIME ZONE 'UTC') AT TIME ZONE COALESCE(tz.ZoneName, N'Eastern Standard Time') AS datetime2(0)) AS StartAtLocal
) AS st;
GO

CREATE OR ALTER VIEW [analytics].[vw_Bookings]
AS
SELECT
    b.[Id]                                                                      AS BookingId,
    b.[EventId]                                                                 AS EventId,
    b.[UserId]                                                                  AS UserId,
    b.[BookedAt]                                                                AS BookedAtUtc,
    CAST((b.[BookedAt] AT TIME ZONE 'UTC') AT TIME ZONE 'Eastern Standard Time' AS date) AS BookedDateLocal,
    b.[Status]                                                                  AS BookingStatus,  -- 1 Active, 2 Cancelled
    CAST(CASE WHEN b.[Status] = 2 THEN 1 ELSE 0 END AS bit)                     AS IsCancelled,
    b.[PaymentStatus]                                                           AS PaymentStatus,  -- 1 Pending, 2 Completed, 3 Failed, 4 Refunded
    b.[PaidAmount]                                                              AS PaidAmount,
    -- Hosts are auto-booked into their own events; exclude these from player booking metrics.
    CAST(CASE WHEN b.[UserId] = ev.HostUserId THEN 1 ELSE 0 END AS bit)         AS IsHostSelfBooking,
    ev.HostUserId,
    ev.IsPaidEvent,
    ev.Price                                                                    AS EventPrice,
    ev.StartAtUtc                                                               AS EventStartAtUtc,
    ev.StartDateLocal                                                           AS EventStartDateLocal,
    ev.StartIsoWeekday                                                          AS EventStartIsoWeekday,
    ev.CityId,
    ev.CityName,
    ev.SportName,
    ev.IsCancelled                                                              AS IsEventCancelled
FROM [dbo].[EventBookings] AS b
INNER JOIN [analytics].[vw_Events] AS ev ON ev.EventId = b.[EventId];
GO

CREATE OR ALTER VIEW [analytics].[vw_Transactions]
AS
SELECT
    t.[Id]                                                                      AS TransactionId,
    t.[EventId]                                                                 AS EventId,
    t.[UserId]                                                                  AS UserId,
    t.[CreatedAt]                                                               AS CreatedAtUtc,
    CAST((t.[CreatedAt] AT TIME ZONE 'UTC') AT TIME ZONE 'Eastern Standard Time' AS date) AS CreatedDateLocal,
    t.[GrossAmount]                                                             AS GrossAmount,
    t.[HostAmount]                                                              AS HostAmount,
    t.[HobioFeeAmount]                                                          AS HobioFeeAmount,
    t.[StripeFeeAmount]                                                         AS StripeFeeAmount,
    t.[Currency]                                                                AS Currency,
    t.[RefundStatus]                                                            AS RefundStatus,   -- 1 NotRefunded, 2 RefundPending, 3 Refunded, 4 RefundFailed, 5 AwaitingDecision, 6 Credited
    -- Money that left (or is leaving) the platform's revenue: refunded, refund in progress, or converted to credit.
    CAST(CASE WHEN t.[RefundStatus] IN (2, 3, 6) THEN 1 ELSE 0 END AS bit)      AS IsReversed,
    ev.HostUserId,
    ev.CityId,
    ev.CityName,
    ev.SportName
FROM [dbo].[Transactions] AS t
INNER JOIN [analytics].[vw_Events] AS ev ON ev.EventId = t.[EventId];
GO

CREATE OR ALTER VIEW [analytics].[vw_Payouts]
AS
SELECT
    p.[Id]                                                                      AS PayoutId,
    p.[HostUserId]                                                              AS HostUserId,
    p.[CreatedAt]                                                               AS CreatedAtUtc,
    CAST((p.[CreatedAt] AT TIME ZONE 'UTC') AT TIME ZONE 'Eastern Standard Time' AS date) AS CreatedDateLocal,
    p.[Amount]                                                                  AS Amount,
    p.[HobioFeeAmount]                                                          AS HobioFeeAmount,
    p.[Status]                                                                  AS PayoutStatus    -- 1 Pending, 2 Completed, 3 Failed
FROM [dbo].[Payouts] AS p;
GO

CREATE OR ALTER VIEW [analytics].[vw_Cities]
AS
SELECT
    c.[Id]      AS CityId,
    c.[Name]    AS CityName,
    co.[Name]   AS CountryName
FROM [dbo].[Cities] AS c
LEFT JOIN [dbo].[Countries] AS co ON co.[Id] = c.[CountryId];
GO
