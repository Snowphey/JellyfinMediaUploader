# Compile le plugin avec le SDK .NET 10, puis ne garde que les DLL à déployer
# (le plugin + TagLibSharp pour lire les tags ; les DLL Jellyfin sont déjà dans le serveur).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out --no-restore \
 && mkdir /dist \
 && cp /out/Jellyfin.Plugin.MediaUploader.dll /dist/ \
 && find /out -maxdepth 1 -iname 'taglib*.dll' -exec cp {} /dist/ \; \
 && ls -l /dist

FROM alpine:3.20
COPY --from=build /dist/ /dist/
# Au lancement : copie les DLL dans le dossier plugins de Jellyfin (volume monté sur /plugin-out).
CMD ["sh", "-c", "mkdir -p /plugin-out && cp /dist/*.dll /plugin-out/ && echo 'Plugin Media Uploader copie dans /plugin-out' && ls /plugin-out"]
