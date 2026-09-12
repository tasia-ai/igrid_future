package main

import (
	"context"
	"log"

	"golang.org/x/sys/windows"
)

// A SYNCHRONIZE handle becomes signaled only when the exact ROBUST process
// exits. Unlike stdin inheritance this remains stable for console/service
// launchers and cannot produce a premature EOF.
func monitorParent(ctx context.Context, pid int, cancel context.CancelFunc) {
	handle, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(pid))
	if err != nil {
		log.Printf("cannot monitor parent %d: %v", pid, err)
		cancel()
		return
	}
	defer windows.CloseHandle(handle)

	done := make(chan struct{})
	go func() {
		windows.WaitForSingleObject(handle, windows.INFINITE)
		close(done)
	}()
	select {
	case <-ctx.Done():
	case <-done:
		log.Printf("parent %d exited", pid)
		cancel()
	}
}
