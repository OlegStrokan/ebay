FROM golang:1.22-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
# modernc.org/sqlite is pure Go, so the binary stays static with CGO disabled.
RUN CGO_ENABLED=0 GOOS=linux go build -o /out/my-ppl ./cmd

FROM alpine:3.20
RUN adduser -D -u 10001 appuser \
    && mkdir -p /data \
    && chown appuser:appuser /data
USER appuser
WORKDIR /data
COPY --from=build /out/my-ppl /usr/local/bin/my-ppl
ENV DB_PATH=/data/ppl.db
EXPOSE 8092
ENTRYPOINT ["/usr/local/bin/my-ppl"]
