/*
    Test fixture: the Tushpa tables the analytics views read, copied from TushpaDB/Tables (column names and
    types as in Tushpa). Foreign keys to tables outside this subset are omitted. Used only by the tests.
    If Tushpa changes one of these columns, update this file and the views together.
*/

CREATE TABLE [dbo].[Countries] (
    [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(255) NOT NULL,
    [Code] NVARCHAR(10) NULL
);
GO

CREATE TABLE [dbo].[Cities] (
    [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(255) NOT NULL,
    [CountryId] INT,
    [TimeZoneId] VARCHAR(50) NOT NULL,
    [CityCode] TINYINT NOT NULL,
    FOREIGN KEY ([CountryId]) REFERENCES [dbo].[Countries]([Id])
);
GO

CREATE TABLE [dbo].[Users] (
    [Id] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [PhoneNumber] VARCHAR(15) NULL,
    [Email] VARCHAR(255) NULL,
    [UniqueIdentifier] NVARCHAR(255) NULL,
    [FirstName] NVARCHAR(25) NOT NULL,
    [LastName] NVARCHAR(25) NOT NULL,
    [DateOfBirth] date NOT NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [OnlineStatus] TINYINT NOT NULL DEFAULT 1,
    [LastOnlineAt] DATETIME2 NULL,
    [ReferralCode] NVARCHAR(25) NOT NULL,
    [ReferredByCode] NVARCHAR(25) NULL,
    [ReferredByUserId] UNIQUEIDENTIFIER NULL,
    [ReferredAt] [datetime] NULL,
    [IsDeleted] BIT NOT NULL DEFAULT 0,
    [DeletedAtUtc] DATETIME2 NULL,
    [ScheduledHardDeleteAtUtc] DATETIME2 NULL,
    [IsEmailVerified] BIT NOT NULL DEFAULT 0,
    [VerificationCode] VARCHAR(64) NULL,
    [EmailVerifiedAtUtc] DATETIME2 NULL,
    [ExpiresAt] DATETIME2 NULL,
    [ProfilePhotoBlurHash] [nvarchar](100) NULL,
    [ProfilePhotoResizedBlobName] [nvarchar](256) NULL,
    [ProfilePhotoThumbnailBlobName] [nvarchar](256) NULL,
    [StripeCustomerId] NVARCHAR(255) NULL,
    [StripeConnectAccountId] NVARCHAR(255) NULL,
    [StripeConnectPayoutsEnabled] BIT NOT NULL DEFAULT 0
);
GO

CREATE TABLE [dbo].[Sports] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(255) NOT NULL,
    [Slug] NVARCHAR(200) NOT NULL
);
GO

CREATE TABLE [dbo].[Venues] (
    [Id] INT PRIMARY KEY IDENTITY,
    [Name] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1024) NULL,
    [Address] NVARCHAR(500) NOT NULL,
    [CityId] INT NOT NULL,
    [IsSystem] BIT NOT NULL DEFAULT 0,
    [PostalCode] NVARCHAR(20),
    [PhotoUrl] NVARCHAR(500),
    [CreatedByUserId] UNIQUEIDENTIFIER NOT NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt] DATETIME2,
    [UpdatedByUserId] UNIQUEIDENTIFIER NULL,
    CONSTRAINT FK_Venues_Cities FOREIGN KEY (CityId) REFERENCES [dbo].[Cities](Id)
);
GO

CREATE TABLE [dbo].[Events] (
    [Id] BIGINT PRIMARY KEY IDENTITY,
    [EventName] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(MAX),
    [SportId] INT NOT NULL,
    [VenueId] INT NOT NULL,
    [IsHostPlaying] BIT NOT NULL,
    [StartDate] DATETIME2 NOT NULL,
    [DurationMinutes] INT NOT NULL,
    [EndDate] DATETIME2 NULL,
    [EventType] TINYINT NOT NULL,
    [SkillLevel] TINYINT NULL,
    [RecurringEventId] INT NULL,
    [IsPaidEvent] BIT NOT NULL,
    [Price] DECIMAL(10,2),
    [TotalAvailableSlots] INT NOT NULL,
    [BookedSlots] INT NOT NULL DEFAULT 0,
    [ArrivalInstructions] NVARCHAR(MAX),
    [ContactInfo] NVARCHAR(255),
    [LocationType] TINYINT NOT NULL,
    [EventStatus] TINYINT NOT NULL,
    [EventCancelationReason] TINYINT NULL,
    [CancelReasonNote] NVARCHAR(255) NULL,
    [CancelationAt] DATETIME2,
    [CreatedByUserId] UNIQUEIDENTIFIER NOT NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt] DATETIME2,
    [UpdatedByUserId] UNIQUEIDENTIFIER NULL,
    [ActivityAccess] TINYINT NOT NULL DEFAULT 1,
    [CancelationOption] TINYINT NOT NULL DEFAULT 1,
    [MinimumPlayersToRun] INT NOT NULL DEFAULT 10,
    [RowVersion] ROWVERSION NOT NULL,
    [TimeZoneId] VARCHAR(50) NOT NULL,
    [RescheduledAtUtc] DATETIME2 NULL,
    CONSTRAINT FK_Event_Venue FOREIGN KEY (VenueId) REFERENCES [dbo].[Venues](Id),
    CONSTRAINT FK_EventOv_Sport FOREIGN KEY (SportId) REFERENCES [dbo].[Sports](Id),
    CONSTRAINT FK_Event_CreatedUser FOREIGN KEY (CreatedByUserId) REFERENCES [dbo].[Users](Id)
);
GO

CREATE TABLE [dbo].[EventBookings] (
    [Id] BIGINT IDENTITY PRIMARY KEY,
    [EventId] BIGINT NOT NULL,
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [BookedAt] DATETIME2 NOT NULL DEFAULT GETDATE(),
    [PaidAmount] DECIMAL(10,2) NOT NULL DEFAULT 0,
    [PaymentStatus] TINYINT NOT NULL DEFAULT 1,
    [Status] TINYINT NOT NULL DEFAULT 1,
    [CancelationAt] DATETIME2,
    [EventBookingCancelReason] TINYINT NULL,
    [Token] NVARCHAR(32) NOT NULL,
    [IsJoined] BIT NOT NULL DEFAULT 0,
    [JoinedDate] DATETIME2 NULL,
    [RSVPStatus] TINYINT NULL,
    [RSVPDate] DATETIME2 NULL,
    CONSTRAINT FK_Booking_Event FOREIGN KEY (EventId) REFERENCES [dbo].[Events](Id),
    CONSTRAINT FK_Booking_User FOREIGN KEY (UserId) REFERENCES [dbo].[Users](Id)
);
GO

CREATE TABLE [dbo].[Transactions] (
    [Id] BIGINT PRIMARY KEY IDENTITY,
    [BookingHoldId] BIGINT NOT NULL,
    [BookingId] BIGINT NOT NULL,
    [EventId] BIGINT NOT NULL,
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [GrossAmount] DECIMAL(10,2) NOT NULL,
    [HostAmount] DECIMAL(10,2) NOT NULL,
    [HobioFeeAmount] DECIMAL(10,2) NOT NULL,
    [StripeFeeAmount] DECIMAL(10,2) NOT NULL,
    [Currency] NVARCHAR(3) NOT NULL DEFAULT 'CAD',
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [InvoiceNumber] INT NULL,
    [RefundStatus] TINYINT NOT NULL DEFAULT 1,
    [RefundedAt] DATETIME2 NULL,
    [StripeRefundId] NVARCHAR(255) NULL,
    [RowVersion] ROWVERSION NOT NULL,
    CONSTRAINT FK_Transaction_Booking FOREIGN KEY (BookingId) REFERENCES [dbo].[EventBookings](Id),
    CONSTRAINT FK_Transaction_Event FOREIGN KEY (EventId) REFERENCES [dbo].[Events](Id),
    CONSTRAINT FK_Transaction_User FOREIGN KEY (UserId) REFERENCES [dbo].[Users](Id)
);
GO

CREATE TABLE [dbo].[Payouts] (
    [Id] BIGINT PRIMARY KEY IDENTITY,
    [HostUserId] UNIQUEIDENTIFIER NOT NULL,
    [Amount] DECIMAL(10,2) NOT NULL,
    [HobioFeeAmount] DECIMAL(10,2) NOT NULL,
    [Status] TINYINT NOT NULL DEFAULT 1,
    [StripeTransferId] NVARCHAR(255) NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_Payouts_HostUser FOREIGN KEY (HostUserId) REFERENCES [dbo].[Users](Id)
);
GO
