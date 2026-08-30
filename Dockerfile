FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/RagKnowledgeBaseApp.Api/RagKnowledgeBaseApp.Api.csproj src/RagKnowledgeBaseApp.Api/
RUN dotnet restore src/RagKnowledgeBaseApp.Api/RagKnowledgeBaseApp.Api.csproj
COPY . .
RUN dotnet publish src/RagKnowledgeBaseApp.Api/RagKnowledgeBaseApp.Api.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Workstation GC keeps the container inside a 1 GiB Container Apps budget;
# Server GC pre-reserves per-core heaps and will trigger restarts at 0.5 vCPU.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "RagKnowledgeBaseApp.Api.dll"]
