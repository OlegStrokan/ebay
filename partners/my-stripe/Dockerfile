FROM golang:1.22-alpine  AS build
WORKDIR /src
COPY go.mod ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -o /out/fintech-sandbox ./cmd/server

FROM gcr.io/distroless/statis-debian12:nonroot
COPY --from=build /out/fintech-sandbox /fintech-sandbox
EXPOSE 8080
USER nonroot:nonroot
ENTRYPOINT ["/fintech-sandbox"]