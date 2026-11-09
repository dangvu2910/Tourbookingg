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
IF USER_ID('identity_app') IS NULL CREATE USER [identity_app] FOR LOGIN [identity_app];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [identity_app];
GO
USE [CatalogDB];
IF USER_ID('catalog_app') IS NULL CREATE USER [catalog_app] FOR LOGIN [catalog_app];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [catalog_app];
GO
USE [BookingDB];
IF USER_ID('booking_app') IS NULL CREATE USER [booking_app] FOR LOGIN [booking_app];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [booking_app];
GO
USE [CustomerDB];
IF USER_ID('customer_app') IS NULL CREATE USER [customer_app] FOR LOGIN [customer_app];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [customer_app];
GO
