# One image, three apps: pass the project to run as a build arg.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src
COPY global.json MobileMoneyGateway.sln ./
COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["sh", "-c", "dotnet $APP_DLL"]
