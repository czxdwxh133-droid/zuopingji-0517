# ============ 构建阶段 ============
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --self-contained false

# ============ 运行阶段 ============
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# 安装 Playwright CLI 和 Chromium 浏览器
RUN dotnet tool install --global Microsoft.Playwright.CLI
ENV PATH="$PATH:/root/.dotnet/tools"
RUN playwright install --with-deps chromium

# Playwright 浏览器路径
ENV PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright

WORKDIR /app
COPY --from=build /app .

# ASP.NET 环境
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}

EXPOSE ${PORT:-8080}

ENTRYPOINT ["dotnet", "资讯速览小助手.dll"]
