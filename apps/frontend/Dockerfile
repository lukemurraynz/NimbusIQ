FROM node:24-alpine AS build
WORKDIR /app

# Build-time environment variables for React Vite build
ARG VITE_CONTROL_PLANE_API_BASE_URL=/api/v1
ENV VITE_CONTROL_PLANE_API_BASE_URL=$VITE_CONTROL_PLANE_API_BASE_URL

COPY package*.json ./
RUN npm install --legacy-peer-deps

COPY . .
# Build SPA (dist/) and server (dist-server/)
RUN npm run build

FROM node:24-alpine AS runtime
WORKDIR /app

# Runtime environment variables
ENV NODE_ENV=production
ENV PROXY_PORT=8080

# Install only production dependencies
COPY package*.json ./
RUN npm install --omit=dev --legacy-peer-deps

# Copy compiled SPA and server from build stage
COPY --from=build /app/dist ./dist
COPY --from=build /app/dist-server ./dist-server

RUN addgroup -S appgroup && adduser -S appuser -G appgroup
RUN chown -R appuser:appgroup /app

EXPOSE 8080

USER appuser

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD node -e "require('http').get('http://localhost:8080/health/ready', (r) => {if (r.statusCode !== 200) throw new Error(r.statusCode)})"

# Start the proxy server
CMD ["node", "dist-server/server.js"]

