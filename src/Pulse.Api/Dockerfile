# Build context is the repository root: docker build -f src/Pulse.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Pulse.Core/Pulse.Core.csproj src/Pulse.Core/
COPY src/Pulse.Api/Pulse.Api.csproj src/Pulse.Api/
RUN dotnet restore src/Pulse.Api/Pulse.Api.csproj
COPY src/Pulse.Core src/Pulse.Core
COPY src/Pulse.Api src/Pulse.Api
RUN dotnet publish src/Pulse.Api/Pulse.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulse.Api.dll"]
