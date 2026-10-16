FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY Tourbooking.sln Tourbooking.csproj ./
COPY services/Identity/Identity.csproj services/Identity/
COPY services/Catalog/Catalog.csproj services/Catalog/
COPY services/Booking/Booking.csproj services/Booking/
COPY services/Notification/Notification.csproj services/Notification/
COPY services/Gateway/Gateway.csproj services/Gateway/
COPY tests/RabbitMq.Tests/RabbitMq.Tests.csproj tests/RabbitMq.Tests/
RUN dotnet restore Tourbooking.sln
COPY . .
FROM source AS build
ARG SERVICE
RUN dotnet publish services/${SERVICE}/${SERVICE}.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
COPY wwwroot ./wwwroot
ARG SERVICE
ENV SERVICE_DLL=${SERVICE}.dll
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
