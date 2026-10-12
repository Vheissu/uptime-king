FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/UptimeKing.Api/UptimeKing.Api.csproj src/UptimeKing.Api/
RUN dotnet restore src/UptimeKing.Api
COPY src/UptimeKing.Api src/UptimeKing.Api
RUN dotnet publish src/UptimeKing.Api -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
RUN mkdir /app/keys && chown $APP_UID /app/keys
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "UptimeKing.Api.dll"]
