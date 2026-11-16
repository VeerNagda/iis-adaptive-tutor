# Frontend image. Build context is adaptive_tutor (see docker-compose.yml).
FROM node:22-alpine
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# NEXT_PUBLIC_* values are baked into the JavaScript at build time. The browser
# (not this container) calls the API, so this must be an address the browser can reach.
ARG NEXT_PUBLIC_API_URL=http://localhost:5242
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL

RUN npm run build
EXPOSE 3000
CMD ["npm", "start"]
