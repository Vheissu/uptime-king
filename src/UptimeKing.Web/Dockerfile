FROM node:24-alpine AS build
WORKDIR /app
COPY src/UptimeKing.Web/package*.json ./
RUN npm ci
COPY src/UptimeKing.Web/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.28-alpine
COPY src/UptimeKing.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/web/browser /usr/share/nginx/html
EXPOSE 8080
