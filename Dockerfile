FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY OracleMcpHttp.slnx .
COPY src/Backend/GeraApiKey/GeraApiKey.csproj src/Backend/GeraApiKey/
COPY src/Backend/McpToolkit/McpToolkit.csproj src/Backend/McpToolkit/
COPY src/Backend/OracleMcpHttp/OracleMcpHttp.csproj src/Backend/OracleMcpHttp/
RUN dotnet restore src/Backend/OracleMcpHttp/OracleMcpHttp.csproj

COPY src/ src/
RUN dotnet publish src/Backend/OracleMcpHttp/OracleMcpHttp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN useradd -m appuser \
    && chown -R appuser:appuser /app
USER appuser

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "OracleMcpHttp.dll"]

## docker build -t filipefk/oracle-mcp:1.0 -t filipefk/oracle-mcp:latest .
## docker push filipefk/oracle-mcp:1.0
## docker push filipefk/oracle-mcp:latest
