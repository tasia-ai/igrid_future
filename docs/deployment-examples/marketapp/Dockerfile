FROM php:8.2-cli-alpine

RUN apk add --no-cache $PHPIZE_DEPS curl-dev \
    && docker-php-ext-install -j"$(nproc)" mysqli curl \
    && apk del $PHPIZE_DEPS

WORKDIR /app

COPY public/ /app/public/
COPY src/ /app/src/
COPY config/ /app/config/

EXPOSE 8099

CMD ["php", "-S", "0.0.0.0:8099", "-t", "/app/public"]
