FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# The browser talks to the API directly, so this is the API address as seen from the host.
ARG VITE_API_URL=http://localhost:5080
ENV VITE_API_URL=$VITE_API_URL
# Sign-in is compiled in when an authority is given (see docker-compose.auth.yml); without one the dashboard has no login.
ARG VITE_AUTH_AUTHORITY=
ARG VITE_AUTH_CLIENT_ID=pulse-web
ENV VITE_AUTH_AUTHORITY=$VITE_AUTH_AUTHORITY VITE_AUTH_CLIENT_ID=$VITE_AUTH_CLIENT_ID
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
