# MyMedicalApp - ASP.NET Core 9 (Linux container)
# Build context: ./MedicalApp

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY MedicalApp.csproj ./
RUN dotnet restore MedicalApp.csproj
COPY . .
RUN dotnet publish MedicalApp.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
# QuestPDF needs fontconfig + at least one font family on Linux.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-liberation curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /app/files
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "MedicalApp.dll"]
