FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine

#COPY publish dockerbot/

EXPOSE 80

WORKDIR /dockerbot

ENTRYPOINT ["dotnet", "DiscordCardLinker.dll"]