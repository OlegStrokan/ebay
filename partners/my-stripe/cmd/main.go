package cmd

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"my-stripe/internal/api"
	"my-stripe/internal/config"
	"my-stripe/internal/store"
	"my-stripe/internal/webhook"
)

func main() {
	logger := log.New(os.Stdout, "", log.LstdFlags|log.LUTC)
	cfg := config.Load()

	st := store.New()
	srv := api.NewServer(cfg, st, logger)
	worker := webhook.NewWorker(cfg, st, logger)

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	go worker.Run(ctx)

	httpServer := &http.Server{
		Addr: ":" + cfg.Port,
		Handler: srv.Handler(),
		ReadHeaderTimeout: 10 * time.Second,
	}

	go func() {
		logger.Printf("[main] my stripe listening on :%s (test_mode)", cfg.Port)
		if err := httpServer.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			logger.Fatalf("[main] http server error: %v", err)
		}
	}()


	<-ctx.Done()
	logger.Printf("[main] shutdown signal received")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := httpServer.Shutdown(shutdownCtx); err != nil {
		logger.Printf("[main] graceful shutdown error: %v", err)
	}
	logger.Printf("[main] bye")
}