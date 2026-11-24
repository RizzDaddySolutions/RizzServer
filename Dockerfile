# Headless RizzServer (OPC UA gateway + REST API)
#   docker build -t rizzserver .
#   docker run -d --name rizzserver -p 4840:4840 -p 5080:5080 -v rizzserver-data:/data rizzserver
#   docker logs rizzserver ; docker exec rizzserver ./rizzserver-service --show-api-key

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY RizzServer.Core/ RizzServer.Core/
COPY RizzServer.Drivers/ RizzServer.Drivers/
COPY RizzServer.Server/ RizzServer.Server/
COPY RizzServer.Service/ RizzServer.Service/
RUN dotnet publish RizzServer.Service -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Project, API key and certificates live in the volume.
ENV RIZZSERVER_DATA_DIR=/data \
    RIZZSERVER_API_URLS=http://0.0.0.0:5080
VOLUME /data
# 5080 = REST API, 4840 = OPC UA (change the port under OPC UA Server settings to publish another one)
EXPOSE 5080 4840

# The API key is generated on first start. Set RIZZSERVER_API_KEY to choose your own.
ENTRYPOINT ["./rizzserver-service"]
