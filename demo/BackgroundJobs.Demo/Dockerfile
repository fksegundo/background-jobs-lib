FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY src/BackgroundJobs/BackgroundJobs.csproj src/BackgroundJobs/
COPY src/BackgroundJobs.AspNetCore/BackgroundJobs.AspNetCore.csproj src/BackgroundJobs.AspNetCore/
COPY demo/BackgroundJobs.Demo/BackgroundJobs.Demo.csproj demo/BackgroundJobs.Demo/
RUN dotnet restore demo/BackgroundJobs.Demo/BackgroundJobs.Demo.csproj
COPY src/ src/
COPY demo/ demo/
RUN dotnet publish demo/BackgroundJobs.Demo/BackgroundJobs.Demo.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "BackgroundJobs.Demo.dll"]
