IF DB_ID('IdentityDB') IS NULL CREATE DATABASE [IdentityDB];
IF DB_ID('CatalogDB') IS NULL CREATE DATABASE [CatalogDB];
IF DB_ID('BookingDB') IS NULL CREATE DATABASE [BookingDB];
IF DB_ID('CustomerDB') IS NULL CREATE DATABASE [CustomerDB];
GO
IF SUSER_ID('identity_app') IS NULL CREATE LOGIN [identity_app] WITH PASSWORD = '$(IDENTITY_SQL_PASSWORD)';
IF SUSER_ID('catalog_app') IS NULL CREATE LOGIN [catalog_app] WITH PASSWORD = '$(CATALOG_SQL_PASSWORD)';
IF SUSER_ID('booking_app') IS NULL CREATE LOGIN [booking_app] WITH PASSWORD = '$(BOOKING_SQL_PASSWORD)';
IF SUSER_ID('customer_app') IS NULL CREATE LOGIN [customer_app] WITH PASSWORD = '$(CUSTOMER_SQL_PASSWORD)';
GO
USE [IdentityDB];
IF SCHEMA_ID('identity') IS NULL EXEC('CREATE SCHEMA [identity]');
IF SCHEMA_ID('identity_read') IS NULL EXEC('CREATE SCHEMA [identity_read]');
IF USER_ID('identity_app') IS NULL CREATE USER [identity_app] FOR LOGIN [identity_app];
DECLARE @table sysname, @target sysname, @sql nvarchar(max);
SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name LIKE 'AspNet%' OR t.name IN ('TourReviews', 'TourReviewVotes', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
    THEN 'identity' ELSE 'identity_read' END
FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
WHILE @table IS NOT NULL
BEGIN
    SET @sql = N'ALTER SCHEMA ' + QUOTENAME(@target) + N' TRANSFER dbo.' + QUOTENAME(@table);
    EXEC sys.sp_executesql @sql;
    SET @table = NULL;
    SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name LIKE 'AspNet%' OR t.name IN ('TourReviews', 'TourReviewVotes', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
        THEN 'identity' ELSE 'identity_read' END
    FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
END
ALTER USER [identity_app] WITH DEFAULT_SCHEMA = [identity];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[identity] TO [identity_app];
-- Projection refresh replaces rows in the service's own read schema (no DDL needed).
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[identity_read] TO [identity_app];
GO
USE [CatalogDB];
IF SCHEMA_ID('catalog') IS NULL EXEC('CREATE SCHEMA [catalog]');
IF SCHEMA_ID('catalog_read') IS NULL EXEC('CREATE SCHEMA [catalog_read]');
IF USER_ID('catalog_app') IS NULL CREATE USER [catalog_app] FOR LOGIN [catalog_app];
DECLARE @table sysname, @target sysname, @sql nvarchar(max);
SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name IN ('Tours', 'SeatReservations', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
    THEN 'catalog' ELSE 'catalog_read' END
FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
WHILE @table IS NOT NULL
BEGIN
    SET @sql = N'ALTER SCHEMA ' + QUOTENAME(@target) + N' TRANSFER dbo.' + QUOTENAME(@table);
    EXEC sys.sp_executesql @sql;
    SET @table = NULL;
    SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name IN ('Tours', 'SeatReservations', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
        THEN 'catalog' ELSE 'catalog_read' END
    FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
END
ALTER USER [catalog_app] WITH DEFAULT_SCHEMA = [catalog];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[catalog] TO [catalog_app];
-- Projection refresh replaces rows in the service's own read schema (no DDL needed).
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[catalog_read] TO [catalog_app];
GO
USE [BookingDB];
IF SCHEMA_ID('booking') IS NULL EXEC('CREATE SCHEMA [booking]');
IF SCHEMA_ID('booking_read') IS NULL EXEC('CREATE SCHEMA [booking_read]');
IF USER_ID('booking_app') IS NULL CREATE USER [booking_app] FOR LOGIN [booking_app];
DECLARE @table sysname, @target sysname, @sql nvarchar(max);
SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name IN ('Bookings', 'Payments', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
    THEN 'booking' ELSE 'booking_read' END
FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
WHILE @table IS NOT NULL
BEGIN
    SET @sql = N'ALTER SCHEMA ' + QUOTENAME(@target) + N' TRANSFER dbo.' + QUOTENAME(@table);
    EXEC sys.sp_executesql @sql;
    SET @table = NULL;
    SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name IN ('Bookings', 'Payments', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
        THEN 'booking' ELSE 'booking_read' END
    FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
END
ALTER USER [booking_app] WITH DEFAULT_SCHEMA = [booking];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[booking] TO [booking_app];
-- Projection refresh replaces rows in the service's own read schema (no DDL needed).
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[booking_read] TO [booking_app];
GO
USE [CustomerDB];
IF SCHEMA_ID('customer') IS NULL EXEC('CREATE SCHEMA [customer]');
IF SCHEMA_ID('customer_read') IS NULL EXEC('CREATE SCHEMA [customer_read]');
IF USER_ID('customer_app') IS NULL CREATE USER [customer_app] FOR LOGIN [customer_app];
DECLARE @table sysname, @target sysname, @sql nvarchar(max);
SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name IN ('ContactInquiries', 'ContactInquiryReplies', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
    THEN 'customer' ELSE 'customer_read' END
FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
WHILE @table IS NOT NULL
BEGIN
    SET @sql = N'ALTER SCHEMA ' + QUOTENAME(@target) + N' TRANSFER dbo.' + QUOTENAME(@table);
    EXEC sys.sp_executesql @sql;
    SET @table = NULL;
    SELECT TOP (1) @table = t.name, @target = CASE WHEN t.name IN ('ContactInquiries', 'ContactInquiryReplies', 'InboxMessages', 'OutboxMessages', '__EFMigrationsHistory')
        THEN 'customer' ELSE 'customer_read' END
    FROM sys.tables AS t WHERE SCHEMA_NAME(t.schema_id) = 'dbo';
END
ALTER USER [customer_app] WITH DEFAULT_SCHEMA = [customer];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[customer] TO [customer_app];
-- Projection refresh replaces rows in the service's own read schema (no DDL needed).
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[customer_read] TO [customer_app];
GO
