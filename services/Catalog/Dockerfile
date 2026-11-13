FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY . .
RUN dotnet restore Tourbooking.sln
FROM source AS build
ARG SERVICE=Catalog
ARG SERVICE_DIR=Catalog
RUN dotnet publish services/${SERVICE_DIR:-$SERVICE}/${SERVICE}.csproj -c Release -o /publish --no-restore -m:1

FROM source AS service-assets
ARG SERVICE=Catalog
ARG SERVICE_DIR=Catalog
RUN set -eu; \
    mkdir -p /service-wwwroot; \
    copy_asset() { mkdir -p "/service-wwwroot/$(dirname "$1")"; cp "/src/Shared/wwwroot/$1" "/service-wwwroot/$1"; }; \
    copy_directory() { mkdir -p "/service-wwwroot/$(dirname "$1")"; cp -R "/src/Shared/wwwroot/$1" "/service-wwwroot/$1"; }; \
    copy_service_assets() { cp -R "/src/services/${SERVICE_DIR:-$SERVICE}/wwwroot/." /service-wwwroot/; }; \
    case "$SERVICE" in \
      Identity) \
        copy_service_assets; copy_asset js/site.js; \
        copy_directory lib/bootstrap; copy_directory lib/jquery; copy_directory lib/jquery-validation; copy_directory lib/jquery-validation-unobtrusive ;; \
      Catalog) \
        copy_service_assets; copy_asset js/site.js; \
        copy_directory lib/bootstrap; copy_directory lib/jquery ;; \
      BookingPlatform) \
        copy_service_assets; copy_asset js/site.js; \
        copy_directory lib/bootstrap; copy_directory lib/jquery; copy_directory lib/jquery-validation; copy_directory lib/jquery-validation-unobtrusive ;; \
      CustomerExperience) \
        copy_service_assets; copy_asset js/site.js; \
        copy_directory lib/bootstrap; copy_directory lib/jquery ;; \
      Gateway) copy_asset favicon.ico; copy_service_assets; copy_directory images ;; \
      Notification) ;; \
      *) echo "Unsupported service asset set: $SERVICE" >&2; exit 1 ;; \
    esac

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
COPY --from=service-assets /service-wwwroot ./wwwroot
ARG SERVICE=Catalog
ARG SERVICE_DIR=Catalog
ENV SERVICE_DLL=${SERVICE}.dll
ENV ASPNETCORE_HTTP_PORTS=8080
RUN mkdir -p /keys /data /app/data /app/wwwroot/images && chown -R app:app /app /keys /data
USER app
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
