# LunchSuggest web app. Built and run by docker-compose (service "web").
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore with the full source: the .NET 10 SDK only pulls the Blazor script package (_framework/blazor.web.js)
# when it can see .razor files, so a "copy .csproj files first" restore layer would publish a site with no Blazor.
COPY src/ src/
RUN dotnet publish src/LunchSuggest.Web/LunchSuggest.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Folder for the keys that protect what pages store in the browser (voter name); a volume keeps them across restarts.
RUN mkdir -p /keys && chown $APP_UID /keys
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "LunchSuggest.Web.dll"]
