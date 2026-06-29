FROM golang:1.22-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
# modernc.org/sqlite is pure Go, so the binary stays static with CGO disabled.
RUN CGO_ENABLED=0 GOOS=linux go build -o /out/my-dpd ./cmd

FROM alpine:3.20
RUN adduser -D -u 10001 appuser \
    && mkdir -p /data \
    && chown appuser:appuser /data
USER appuser
WORKDIR /data
COPY --from=build /out/my-dpd /usr/local/bin/my-dpd
ENV DB_PATH=/data/dpd.db
EXPOSE 8091
ENTRYPOINT ["/usr/local/bin/my-dpd"]
