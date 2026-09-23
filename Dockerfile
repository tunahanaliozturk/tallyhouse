# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source

# Restore before the rest of the source is copied, so editing a .cs file does not invalidate the restore
# layer. The .editorconfig comes along because analyzer severities live in it and the build treats warnings
# as errors, so leaving it out makes the image build fail where a local build passes.
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/Tallyhouse.Domain/Tallyhouse.Domain.csproj src/Tallyhouse.Domain/
COPY src/Tallyhouse.Application/Tallyhouse.Application.csproj src/Tallyhouse.Application/
COPY src/Tallyhouse.Infrastructure/Tallyhouse.Infrastructure.csproj src/Tallyhouse.Infrastructure/
COPY src/Tallyhouse.Api/Tallyhouse.Api.csproj src/Tallyhouse.Api/
COPY src/Tallyhouse.Loader/Tallyhouse.Loader.csproj src/Tallyhouse.Loader/
RUN dotnet restore src/Tallyhouse.Api/Tallyhouse.Api.csproj \
 && dotnet restore src/Tallyhouse.Loader/Tallyhouse.Loader.csproj

COPY src/ src/

RUN dotnet publish src/Tallyhouse.Api/Tallyhouse.Api.csproj --configuration Release --no-restore --output /app/api \
 && dotnet publish src/Tallyhouse.Loader/Tallyhouse.Loader.csproj --configuration Release --no-restore --output /app/loader

# Two images from one build: the collector and the loader scale and fail independently, which is the point
# of putting Kafka between them, but they share every line of infrastructure code.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# The runtime image has neither curl nor wget, so the container health check is a short bash script that
# speaks HTTP over /dev/tcp rather than a network client installed for one request.
COPY --chmod=755 docker/healthcheck.sh /usr/local/bin/healthcheck

# The image ships a non-root user. The only reason services still run as root is that nobody changed it.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

FROM runtime AS api
COPY --from=build /app/api .
ENTRYPOINT ["dotnet", "Tallyhouse.Api.dll"]

FROM runtime AS loader
COPY --from=build /app/loader .
ENTRYPOINT ["dotnet", "Tallyhouse.Loader.dll"]
