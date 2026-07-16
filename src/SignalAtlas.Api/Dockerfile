# Multi-stage build for the Signal Atlas API (.NET 10). OPTIONAL — see docker-compose.yml header;
# the edge node runs fully offline without Docker (SPEC §4.3/§15).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the whole tree and restore against the solution (keeps project graph intact).
COPY . .
RUN dotnet restore SignalAtlas.slnx
RUN dotnet publish src/SignalAtlas.Api/SignalAtlas.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# OPTIONAL field-hardware note: to use a real HackRF over USB from inside the container, run with
# USB passthrough — `docker run --device=/dev/bus/usb ...` or add a compose `devices:` mapping
# (see docker-compose.yml). Without it the API falls back to the file/synthetic sample source.
# RECEIVE-ONLY: no transmit path is ever exposed (SPEC §4.2 L1).

ENTRYPOINT ["dotnet", "SignalAtlas.Api.dll"]
