FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ScreenReviewServer.csproj ./
RUN dotnet restore ScreenReviewServer.csproj

COPY . ./
RUN dotnet publish ScreenReviewServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

EXPOSE 10000
ENTRYPOINT ["dotnet", "ScreenReviewServer.dll"]
