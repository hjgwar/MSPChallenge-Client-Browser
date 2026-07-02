# To build and run this Dockerfile, use the following commands:
#   git clean -d -x -f; docker build -t mspchallenge-client-browser .
# Or, for MSP hub:
#   git clean -d -x -f; docker build -t docker-hub.mspchallenge.info/cradlewebmaster/mspchallenge-client-browser:dev .
# To run the container, use:
#   docker run --rm -d -p 5261:5261 mspchallenge-client-browser
# Or, from MSP hub:
#   docker run --rm -d -p 5261:5261 docker-hub.mspchallenge.info/cradlewebmaster/mspchallenge-client-browser:dev

# Multi-stage build for ASP.NET Core Blazor Server app (.NET 10)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first to leverage Docker layer caching for restore
COPY ["MSPChallenge-Client-Browser.csproj", "./"]
RUN dotnet restore "MSPChallenge-Client-Browser.csproj"

# Copy the remaining source and publish
COPY . .
RUN dotnet publish "MSPChallenge-Client-Browser.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Use port 6261 to match local development environment
ENV ASPNETCORE_URLS=http://+:5261
EXPOSE 5261

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MSPChallenge-Client-Browser.dll"]

