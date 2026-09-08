FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ZenConnect.csproj ./
RUN dotnet restore ZenConnect.csproj -p:TargetFramework=net8.0

COPY . ./
RUN dotnet publish ZenConnect.csproj -c Release -o /app/publish -p:TargetFramework=net8.0 --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

EXPOSE 10000
ENTRYPOINT ["dotnet", "ZenConnect.dll", "--server"]
