# Build context is the repository root: docker build -f src/Pulse.Simulator/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Pulse.Core/Pulse.Core.csproj src/Pulse.Core/
COPY src/Pulse.Simulator/Pulse.Simulator.csproj src/Pulse.Simulator/
RUN dotnet restore src/Pulse.Simulator/Pulse.Simulator.csproj
COPY src/Pulse.Core src/Pulse.Core
COPY src/Pulse.Simulator src/Pulse.Simulator
RUN dotnet publish src/Pulse.Simulator/Pulse.Simulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Pulse.Simulator.dll"]
