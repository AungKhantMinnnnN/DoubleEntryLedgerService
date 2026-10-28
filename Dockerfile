FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ledger.Api/Ledger.Api.csproj src/Ledger.Api/
RUN dotnet restore src/Ledger.Api/Ledger.Api.csproj
COPY src/Ledger.Api src/Ledger.Api
RUN dotnet publish src/Ledger.Api/Ledger.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
