FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["CookieSheetServer/CookieSheetServer.csproj", "CookieSheetServer/"]
COPY ["CookieSheet.Refunds/CookieSheet.Refunds.csproj", "CookieSheet.Refunds/"]
RUN dotnet restore "CookieSheetServer/CookieSheetServer.csproj"
COPY . .
WORKDIR "/src/CookieSheetServer"
RUN dotnet build "./CookieSheetServer.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./CookieSheetServer.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CookieSheetServer.dll"]
