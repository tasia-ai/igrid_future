//go:build !windows

package main

import (
	"context"
	"log"
	"syscall"
	"time"
)

func monitorParent(ctx context.Context, pid int, cancel context.CancelFunc) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			if syscall.Kill(pid, 0) != nil {
				log.Printf("parent %d exited", pid)
				cancel()
				return
			}
		}
	}
}
